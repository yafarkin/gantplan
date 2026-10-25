using System.Diagnostics;
using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using Google.OrTools.Sat;
using Google.OrTools.Util;
using LinearExpr = Google.OrTools.Sat.LinearExpr;

namespace GantPlan.Logic;

// Строит и решает CP-SAT модель (Google OR-Tools) распределения задач по
// ресурсам с учётом календарей (выходные/отпуска), зависимостей между
// задачами, ограничений по датам и приоритетов. Результат - расчётные
// start/end каждой задачи в днях от начала проекта, переведённые обратно
// в даты и разложенные по ресурсам.
//
// План и факт. Fact.Records - журнал хода работ. Вручную вводится то, что
// известно: старт, остаток, пауза и - обязательно - закрытие (Completed).
// По FactDate солвер сам дописывает автоматический прогресс (IsAuto):
// старт по плану и остаток на дату среза. Перед каждым решением авто-записи
// удаляются и считаются заново - планирование опирается только на ручные.
// Закрытие солвер сам не ставит никогда: незакрытая задача не может
// закончиться раньше ближайшего рабочего дня после FactDate (см.
// CalcTaskEnd) - если по плану она уже должна была быть сделана, она
// "ждёт подтверждения закрытия" и держит исполнителя и последователей.
public sealed class Solver
{
    // Потолок горизонта планирования (~10 лет) - защита от вырожденных
    // входных данных, а не реальное ограничение.
    private const int MaxHorizon = 3660;

    private ProjectDto _project = null!;

    private TaskAlignment _taskAlignment = null!;

    // Веса приоритетов для целевой функции: чем выше приоритет задачи, тем
    // сильнее штраф за поздний старт (см. CreateStartEndVarsAndPriorityObjectives),
    // поэтому солвер стремится начинать такие задачи как можно раньше.
    // Приоритет - сильное пожелание, а не жёсткий порядок: жёсткий порядок
    // задаётся через PredecessorIds.
    private readonly Dictionary<int, long> _weights = new()
    {
        {1, 100000000}, // Priority 1
        {2, 100000}, // Priority 2
        {3, 100}, // Priority 3
        {0, 1} // Without priority
    };

    private CpModel _model = null!;
    private readonly Dictionary<string, IntVar> _starts = new();
    private readonly Dictionary<string, IntVar> _ends = new();
    private readonly Dictionary<(string task, string person), BoolVar> _personUses = new();
    private readonly Dictionary<string, List<IntervalVar>> _personIntervals = new();
    private readonly List<LinearExpr> _priorityObjectives = new();

    // Календари ресурсов на весь MaxHorizon - общие для жадного плана,
    // модели и разбора результата.
    private readonly Dictionary<string, CalendarLogic> _calendars = new();

    // Уже начатые (по ручным записям факта) задачи: старт, исполнитель и
    // остаток известны, в модели это константы (см. PrepareFixedTasks).
    private readonly Dictionary<string, FixedTask> _fixedTasks = new();

    // Граница истории исполнителя: день после его последнего подтверждённого
    // закрытия (см. CalcHistoryEnd).
    private readonly Dictionary<string, int> _historyEnd = new();

    // FactDate в днях от ProjectStart; null - даты среза нет.
    private int? _factOffset;

    // Предупреждения о входных данных (копятся при подготовке) и о
    // DueDate (пересчитываются при каждой перестройке модели, см.
    // CreateDependencyBetweenTasks) - отдельно, чтобы перестройка при
    // нехватке горизонта не теряла первые и не дублировала вторые.
    private readonly List<string> _warnings = new();
    private readonly List<string> _dueDateWarnings = new();
    private readonly List<TaskDto> _awaitingClosure = new();

    // См. BuildGreedyPlan.
    private Dictionary<string, (int Start, int End, string Resource)>? _greedyPlan;

    // Итог последнего Solve - для диагностики в консоли. Optimal - лучший
    // план доказан; Feasible - план найден, но лимит времени кончился до
    // доказательства оптимальности (следующий прогон может дать другой);
    // Infeasible - ограничения противоречат друг другу; Unknown - за лимит
    // времени не нашлось вообще ни одного плана (это ещё не значит, что
    // его нет).
    public CpSolverStatus Status { get; private set; }

    // Статус второго этапа - выбора между равноценными планами (см.
    // SolveTieBreak); null, если этап не запускался (первый этап не
    // доказал оптимум или не нашёл план).
    public CpSolverStatus? TieBreakStatus { get; private set; }

    public double ObjectiveValue { get; private set; }

    public double WallTimeSeconds { get; private set; }

    // Горизонт планирования в днях от ProjectStart - все start/end переменные
    // солвера ограничены диапазоном [0, Horizon]. Считается под проект
    // автоматически (см. CalcHorizon).
    public int Horizon { get; private set; }

    // Предупреждения о том, что в план заложить не удалось (например,
    // недостижимый DueDate) - решение при этом найдено.
    public IReadOnlyList<string> Warnings => [.._warnings, .._dueDateWarnings];

    // Задачи, которые по плану уже должны были закончиться, но их закрытие
    // не подтверждено (см. TaskPlanDto.AwaitingClosure).
    public IReadOnlyList<TaskDto> AwaitingClosure => _awaitingClosure;

    public bool Solve(ProjectDto project, int? maxSeconds = null)
    {
        _project = project;
        TieBreakStatus = null;
        _warnings.Clear();
        _dueDateWarnings.Clear();
        _awaitingClosure.Clear();

        // Авто-записи прошлого прогона (если план уже решали) - не факт, а
        // расчёт: убираем, чтобы планировать только по ручным записям.
        RemoveAutoFacts(_project.RootTask);

        // Разворачивает дерево задач проекта в плоский список задач,
        // которые нужно планировать (с учётом приоритетов/весов).
        _taskAlignment = new TaskAlignment();
        _taskAlignment.Alignment(_project, _weights);

        _factOffset = _project.FactDate is { } factDate ? Offset(factDate) : null;
        BuildCalendars();
        CalcHistoryEnd();
        PrepareFixedTasks();

        // Горизонт - по жадному плану с запасом (см. CalcHorizon). Если
        // какая-то задача всё же в него не влезла - удваиваем и строим
        // модель заново.
        _greedyPlan = BuildGreedyPlan();
        Horizon = CalcHorizon();
        while (true)
        {
            try
            {
                _model = new CpModel();
                // Строит все переменные и ограничения модели (см. PrepareSolverConstraints).
                PrepareSolverConstraints();
                AddGreedyHints();
                break;
            }
            catch (HorizonExceededException) when (Horizon < MaxHorizon)
            {
                Horizon = Math.Min(MaxHorizon, Horizon * 2);
            }
        }

        // Цель солвера - минимизировать общий срок проекта (makespan, то есть
        // максимальный end среди всех планируемых задач) плюс сумму
        // приоритетных штрафов за поздний старт (см. _weights).
        var makespan = _model.NewIntVar(0, Horizon, "makespan");
        _model.AddMaxEquality(makespan, _taskAlignment.FlattenTasksToSolve.Select(t => _ends[t.Key]).ToArray());
        var objective = makespan + LinearExpr.Sum(_priorityObjectives);
        _model.Minimize(objective);

        var solver = CreateCpSolver(maxSeconds);

        Status = solver.Solve(_model);
        WallTimeSeconds = solver.WallTime();

        var solved = Status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;

        if (solved)
        {
            ObjectiveValue = solver.ObjectiveValue;

            if (Status == CpSolverStatus.Optimal)
            {
                solver = SolveTieBreak(solver, objective, maxSeconds);
            }

            PostFillAfterSolve(solver);
        }

        return solved;
    }

    private int Offset(DateOnly date) => date.DayNumber - _project.ProjectStart.DayNumber;

    private DateOnly ToDate(int offset) => _project.ProjectStart.AddDays(offset);

    private static void RemoveAutoFacts(TaskDto? task)
    {
        if (task is null)
        {
            return;
        }

        if (task.Fact is not null)
        {
            task.Fact.Records.RemoveAll(r => r.IsAuto);
            if (task.Fact.Records.Count == 0)
            {
                task.Fact = null;
            }
        }

        foreach (var child in task.Children)
        {
            RemoveAutoFacts(child);
        }
    }

    private void BuildCalendars()
    {
        _calendars.Clear();
        foreach (var resource in _project.Resources)
        {
            _calendars[resource.Name] =
                new CalendarLogic(MaxHorizon, _project.ProjectStart, _project.GlobalCalendar, resource.Calendar);
        }
    }

    // Граница истории исполнителя - день после его последнего подтверждённого
    // закрытия: раньше неё ему не ставится ничего неначатого. Закрытые задачи
    // в модели не участвуют (CanSkipTask), и без этой границы их время в
    // прошлом выглядело бы свободным - солвер ставил бы туда совсем другие
    // задачи. Совсем новую задачу от постановки "задним числом" позже
    // последнего закрытия защищает StartAfter.
    private void CalcHistoryEnd()
    {
        _historyEnd.Clear();
        foreach (var task in _taskAlignment.OriginalTasks.Values.Where(t => !t.HasChild))
        {
            var completed = task.Fact?.Records.LastOrDefault(r => r.Type == TaskFactRecordType.Completed);
            if (completed is null)
            {
                continue;
            }

            if (FindCompletedBy(task) is not { } resource)
            {
                _warnings.Add($"Задача {task.Id} закрыта, но неизвестно, кем: граница истории исполнителя " +
                              "не учтена - укажите ResourceName в записи Completed");
                continue;
            }

            var end = Offset(completed.RecordedAt) + 1;
            _historyEnd[resource.Name] = Math.Max(_historyEnd.GetValueOrDefault(resource.Name), end);
        }
    }

    // Кто делал закрытую задачу: из записи о закрытии, иначе из последней
    // записи с исполнителем, иначе из лимитов или заданного руками Plan.
    // Если имени нигде нет, но в лимитах роль, и человек этой роли в
    // проекте один - значит, он. null - определить нельзя.
    private ResourceDto? FindCompletedBy(TaskDto task)
    {
        var name = task.Fact?.Records.LastOrDefault(r => r.Type == TaskFactRecordType.Completed)?.ResourceName
                   ?? task.Fact?.Records.LastOrDefault(r => r.ResourceName is not null)?.ResourceName
                   ?? task.Limit?.ResourceName
                   ?? task.Plan?.ResourceName;

        if (name is not null)
        {
            return _project.Resources.FirstOrDefault(r => r.Name == name);
        }

        var byRole = _project.Resources.Where(r => r.Role == task.Limit?.ResourceRole).ToList();
        return byRole.Count == 1 ? byRole[0] : null;
    }

    // Уже начатая задача: старт - дата последней ручной записи прогресса
    // (Started - работа в этот день только начата; прочие записи - день уже
    // отработан, остаток считается со следующего дня), сдвинутая на
    // ближайший рабочий день исполнителя; исполнитель - тот, кто её делал
    // (см. FindResourcesForTask); остаток - из той же записи или, если он не
    // указан, из лимитов задачи.
    private void PrepareFixedTasks()
    {
        _fixedTasks.Clear();
        foreach (var (taskKey, task) in _taskAlignment.FlattenTasksToSolve)
        {
            if (task.Fact?.IsProgress != true)
            {
                continue;
            }

            var resource = FindResourcesForTask(task).Single();
            var lastProgressRec = FindLastProgressRecord(task.Fact);

            var start = Offset(lastProgressRec.RecordedAt);
            if (lastProgressRec.Type != TaskFactRecordType.Started)
            {
                start++;
            }

            start = (_calendars[resource.Name].CalcEndOffset(start, 1)
                     ?? throw new Exception($"Task {taskKey} is in progress, but its resource has no working days ahead")) - 1;

            var duration = lastProgressRec.Duration ?? CalcTaskLimitDuration(task, resource);
            _fixedTasks[taskKey] = new FixedTask(start, duration, resource);
        }
    }

    private sealed record FixedTask(int Start, int Duration, ResourceDto Resource);

    // Конец задачи (полуинтервал [start, end)) у исполнителя при старте в
    // start: duration рабочих дней по его календарю. Плюс правило закрытия:
    // незакрытая задача, начатая не позже ближайшего рабочего дня после
    // FactDate, не может закончиться раньше этого дня - если по плану она
    // уже должна была быть сделана, она тянется до него (ждёт
    // подтверждения закрытия). null - не влезает в MaxHorizon.
    private int? CalcTaskEnd(ResourceDto resource, int start, int duration)
    {
        var calendar = _calendars[resource.Name];
        if (calendar.CalcEndOffset(start, duration) is not { } end)
        {
            return null;
        }

        if (_factOffset is { } factOffset)
        {
            if (calendar.CalcEndOffset(factOffset + 1, 1) is not { } closureEnd)
            {
                return null;
            }

            if (start < closureEnd)
            {
                end = Math.Max(end, closureEnd);
            }
        }

        return end;
    }

    private static CpSolver CreateCpSolver(double? maxSeconds)
    {
        var solver = new CpSolver();

        if (maxSeconds is not null)
        {
            solver.StringParameters = FormattableString.Invariant($"max_time_in_seconds:{maxSeconds}");
        }

        return solver;
    }

    // Второй этап. Планов с одной и той же оптимальной целью обычно
    // несколько (например, две одинаковые задачи одного исполнителя можно
    // поставить в любом порядке), и солвер выбирает между ними как
    // получится - после мелкой правки входных данных план "прыгает".
    // Здесь цель первого этапа фиксируется на найденном оптимуме, и среди
    // таких планов выбирается однозначный: раньше стартуют задачи, которые
    // раньше идут в дереве, а при прочих равных берётся исполнитель, который
    // раньше в списке Resources. Отдельный этап, а не слагаемое в общей
    // цели: иначе пришлось бы домножать и без того большие веса
    // приоритетов, и коэффициенты переполнили бы long.
    // Возвращает солвер, из которого брать итоговое решение.
    private CpSolver SolveTieBreak(CpSolver firstStage, LinearExpr objective, int? maxSeconds)
    {
        _model.Add(objective <= (long)Math.Round(firstStage.ObjectiveValue));

        // Подсказка - решение первого этапа: оно заведомо допустимо.
        _model.ClearHints();
        foreach (var start in _starts.Values)
        {
            _model.AddHint(start, firstStage.Value(start));
        }

        foreach (var use in _personUses.Values)
        {
            _model.AddHint(use, firstStage.BooleanValue(use));
        }

        var tasks = _taskAlignment.FlattenTasksToSolve.Keys.ToList();
        var resourceIndex = _project.Resources
            .Select((r, i) => (r.Name, i))
            .ToDictionary(x => x.Name, x => x.i);

        // Чем раньше задача в дереве, тем больше её вес - так из двух
        // одинаковых задач раньше стартует первая, а первому в списке
        // исполнителю достаётся первая задача (вес исполнителя домножен на
        // вес задачи, иначе перестановка "A->john, B->doe" на "A->doe,
        // B->john" стоила бы столько же). Порядок стартов важнее выбора
        // исполнителя: множитель стартов больше максимально возможной
        // суммы по исполнителям.
        var taskWeight = tasks
            .Select((t, i) => (t, w: (long)(tasks.Count - i)))
            .ToDictionary(x => x.t, x => x.w);

        var resourceTerm = LinearExpr.WeightedSum(
            _personUses.Values,
            _personUses.Keys.Select(k => resourceIndex[k.person] * taskWeight[k.task]));
        var resourceTermMax = (long)tasks.Count * tasks.Count * _project.Resources.Count + 1;

        var startTerm = LinearExpr.WeightedSum(
            tasks.Select(t => _starts[t]),
            tasks.Select(t => taskWeight[t]));

        _model.Minimize(startTerm * resourceTermMax + resourceTerm);

        var remaining = maxSeconds is null ? (double?)null : Math.Max(1, maxSeconds.Value - WallTimeSeconds);
        var solver = CreateCpSolver(remaining);
        var status = solver.Solve(_model);
        WallTimeSeconds += solver.WallTime();
        TieBreakStatus = status;

        return status is CpSolverStatus.Optimal or CpSolverStatus.Feasible ? solver : firstStage;
    }

    // Жадный план: задачи по одной, в порядке готовности (все
    // предшественники уже расставлены), а среди готовых - по приоритету и
    // порядку в дереве; каждая ставится тому из подходящих исполнителей, у
    // кого она раньше закончится, в первое свободное окно. Соблюдает те же
    // жёсткие ограничения, что и модель, кроме DueDate. Нужен для двух
    // вещей: его срок - основа горизонта (см. CalcHorizon), а сам план -
    // подсказка солверу (см. AddGreedyHints), чтобы у того сразу было
    // допустимое решение, а не поиск с нуля.
    // null - если какую-то задачу не удалось поставить в MaxHorizon.
    private Dictionary<string, (int Start, int End, string Resource)>? BuildGreedyPlan()
    {
        // Занятость исполнителя - как в CreateResourceRestrictions: до
        // AvailFrom и после AvailTo он недоступен.
        var busy = _project.Resources.ToDictionary(r => r.Name, r =>
        {
            var intervals = new List<(int Start, int End)>();
            if (r.AvailFrom is not null)
            {
                intervals.Add((int.MinValue, Offset(r.AvailFrom.Value)));
            }

            if (r.AvailTo is not null)
            {
                intervals.Add((Offset(r.AvailTo.Value) + 1, int.MaxValue));
            }

            return intervals;
        });

        var plan = new Dictionary<string, (int Start, int End, string Resource)>();
        var tasks = _taskAlignment.FlattenTasksToSolve;

        // Уже начатые задачи стоят там, где стоят.
        foreach (var (taskKey, fixedTask) in _fixedTasks)
        {
            if (CalcTaskEnd(fixedTask.Resource, fixedTask.Start, fixedTask.Duration) is not { } end)
            {
                return null;
            }

            plan[taskKey] = (fixedTask.Start, end, fixedTask.Resource.Name);
            busy[fixedTask.Resource.Name].Add((fixedTask.Start, end));
        }

        var treeOrder = tasks.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

        while (plan.Count < tasks.Count)
        {
            var (taskKey, task) = tasks
                .Where(t => !plan.ContainsKey(t.Key) && t.Value.Limit!.PredecessorIds.All(plan.ContainsKey))
                .OrderByDescending(t => _weights[t.Value.Limit!.Priority ?? 0])
                .ThenBy(t => treeOrder[t.Key])
                .First();

            var earliest = Math.Max(0, CalcMinStart(task));
            foreach (var predecessorId in task.Limit!.PredecessorIds)
            {
                earliest = Math.Max(earliest, plan[predecessorId].End);
            }

            (int Start, int End, string Resource)? best = null;
            foreach (var resource in FindResourcesForTask(task))
            {
                var slot = FindFreeSlot(resource, busy[resource.Name],
                    Math.Max(earliest, _historyEnd.GetValueOrDefault(resource.Name)),
                    CalcTaskLimitDuration(task, resource));
                if (slot is not null && (best is null || slot.Value.End < best.Value.End))
                {
                    best = (slot.Value.Start, slot.Value.End, resource.Name);
                }
            }

            if (best is null)
            {
                return null;
            }

            plan[taskKey] = best.Value;
            busy[best.Value.Resource].Add((best.Value.Start, best.Value.End));
        }

        return plan;
    }

    // Первое окно не раньше earliest: старт в рабочий день, конец - по
    // CalcTaskEnd, без пересечения с занятостью исполнителя.
    private (int Start, int End)? FindFreeSlot(
        ResourceDto resource, List<(int Start, int End)> busy, int earliest, int duration)
    {
        var calendar = _calendars[resource.Name];
        var start = earliest;
        while (start < MaxHorizon)
        {
            if (!calendar.IsWorkingDay(start))
            {
                start++;
                continue;
            }

            if (CalcTaskEnd(resource, start, duration) is not { } end)
            {
                return null;
            }

            var s = start;
            var conflictEnd = busy.Where(b => b.Start < end && s < b.End).Select(b => (int?)b.End).Max();
            if (conflictEnd is null)
            {
                return (start, end);
            }

            if (conflictEnd.Value >= MaxHorizon)
            {
                return null;
            }

            start = conflictEnd.Value;
        }

        return null;
    }

    // Горизонт = срок жадного плана с запасом: оптимальный план обычно
    // короче жадного, но может и чуть выйти за него (например, задачу без
    // приоритета выгодно отодвинуть подальше, чтобы приоритетная начала
    // раньше). Слишком щедрый горизонт не бесплатен - модель растёт
    // пропорционально ему, и солвер ищет план заметно дольше.
    private int CalcHorizon()
    {
        if (_greedyPlan is null)
        {
            return MaxHorizon;
        }

        var makespan = _greedyPlan.Values.Select(p => p.End).DefaultIfEmpty(0).Max();

        return Math.Clamp(makespan + Math.Max(14, makespan / 4), 1, MaxHorizon);
    }

    // Подсказка солверу - жадный план (см. BuildGreedyPlan). Если он
    // нарушает DueDate, солвер просто отбросит подсказку и будет искать сам.
    private void AddGreedyHints()
    {
        if (_greedyPlan is null)
        {
            return;
        }

        foreach (var (taskKey, (start, _, resource)) in _greedyPlan)
        {
            if (_fixedTasks.ContainsKey(taskKey))
            {
                continue;
            }

            _model.AddHint(_starts[taskKey], start);
            foreach (var person in _project.Resources)
            {
                if (_personUses.TryGetValue((taskKey, person.Name), out var use))
                {
                    _model.AddHint(use, person.Name == resource);
                }
            }
        }
    }

    private sealed class HorizonExceededException(string message) : Exception(message);

    // Переносит найденное солвером решение обратно в доменные объекты:
    // переводит целочисленные start/end в даты, определяет исполнителя
    // каждой задачи и дописывает автоматический прогресс по FactDate (см.
    // AddAutoFacts). Plan у планируемых задач всегда перезаписывается -
    // это результат расчёта, а не входные данные.
    private void PostFillAfterSolve(CpSolver solver)
    {
        foreach (var (key, task) in _taskAlignment.OriginalTasks)
        {
            if (!_starts.ContainsKey(key))
            {
                continue;
            }

            var start = (int)solver.Value(_starts[key]);
            var end = (int)solver.Value(_ends[key]);

            // Исполнитель: у начатой задачи - тот, кто её делает (менять
            // исполнителя "задним числом" нельзя); иначе - тот ресурс, для
            // которого солвер выставил use-переменную в 1 (см.
            // CreateOptionalTaskInterval).
            var fixedTask = _fixedTasks.GetValueOrDefault(key);
            var resource = fixedTask?.Resource
                           ?? _project.Resources.FirstOrDefault(r =>
                               _personUses.TryGetValue((key, r.Name), out var use) && solver.Value(use) == 1)
                           ?? throw new Exception($"Can't find resource for task {key}");

            var duration = fixedTask?.Duration ?? CalcTaskLimitDuration(task, resource);

            // Конец "по рабочим дням" раньше фактического - значит, задачу
            // дотянуло правило закрытия (см. CalcTaskEnd).
            var awaitingClosure = end > _calendars[resource.Name].CalcEndOffset(start, duration);

            task.Plan = new TaskPlanDto
            {
                ResourceName = resource.Name,
                PlannedStart = ToDate(start),
                // end - день, следующий за последним днём задачи.
                PlannedFinish = ToDate(end - 1),
                AwaitingClosure = awaitingClosure
            };

            if (awaitingClosure)
            {
                _awaitingClosure.Add(task);
            }

            AddAutoFacts(task, start, end, duration, resource, isStarted: fixedTask is not null);
        }

        FillFinishedTasksPlan();
        PostFillContainerTasks(_project.RootTask);
    }

    // Автоматический прогресс на дату среза (записи с IsAuto, см.
    // TaskFactRecordDto.IsAuto): если по плану задача к FactDate уже начата,
    // а вручную её старт не отмечен - Started на плановую дату старта; если
    // FactDate попадает внутрь задачи - InProgress на FactDate с остатком
    // рабочих дней после неё. Completed не пишется никогда - закрытие
    // подтверждается только вручную.
    private void AddAutoFacts(TaskDto task, int start, int end, int duration, ResourceDto resource, bool isStarted)
    {
        if (_factOffset is not { } factOffset || start > factOffset)
        {
            return;
        }

        task.Fact ??= new TaskFactDto();

        if (!isStarted)
        {
            task.Fact.Records.Add(new TaskFactRecordDto
            {
                RecordedAt = ToDate(start),
                Type = TaskFactRecordType.Started,
                ResourceName = resource.Name,
                Duration = duration,
                IsAuto = true
            });
        }

        // Последний день задачи по построению (CalcTaskEnd) всегда позже
        // FactDate - незакрытая задача на дату среза всегда ещё в работе.
        var lastProgressRec = FindLastProgressRecord(task.Fact);
        if (lastProgressRec.RecordedAt < _project.FactDate)
        {
            var remaining = _calendars[resource.Name].CalcWorkingDaysCount(ToDate(factOffset + 1), ToDate(end - 1));

            task.Fact.Records.Add(new TaskFactRecordDto
            {
                RecordedAt = _project.FactDate!.Value,
                Type = TaskFactRecordType.InProgress,
                ResourceName = resource.Name,
                Duration = remaining,
                IsAuto = true
            });
        }
    }

    // Закрытые задачи в модели не участвуют. Если даты им не заданы руками,
    // восстанавливаем их для таймлайна: конец - дата закрытия, начало - дата
    // ручного Started или, если её нет, отсчёт оценки назад по календарю
    // исполнителя. Иначе после подтверждения закрытия задача просто
    // пропадала бы с диаграммы.
    private void FillFinishedTasksPlan()
    {
        foreach (var task in _taskAlignment.OriginalTasks.Values.Where(t => !t.HasChild))
        {
            var completed = task.Fact?.Records.LastOrDefault(r => r.Type == TaskFactRecordType.Completed);
            if (completed is null || task.Plan is { PlannedStart: not null, PlannedFinish: not null })
            {
                continue;
            }

            var resource = FindCompletedBy(task);
            var finish = completed.RecordedAt;
            var start = task.Fact!.StartDate
                        ?? (resource is null || task.Limit is null
                            ? finish
                            : ToDate(_calendars[resource.Name].CalcStartOffset(Offset(finish), CalcTaskLimitDuration(task, resource))));

            task.Plan = new TaskPlanDto
            {
                ResourceName = task.Plan?.ResourceName ?? resource?.Name,
                PlannedStart = start,
                PlannedFinish = finish
            };
        }
    }

    // У составных (родительских) задач нет собственных start/end в модели -
    // солвер планирует только листовые задачи. План родителя - это агрегат
    // по дочерним: минимальное начало и максимальное окончание, рекурсивно
    // снизу вверх.
    private void PostFillContainerTasks(TaskDto task)
    {
        if (!task.HasChild)
        {
            return;
        }

        foreach (var child in task.Children)
        {
            PostFillContainerTasks(child);
        }

        var startDate = task.Children.Where(c => c.Plan is not null).Min(c => c.Plan!.PlannedStart);
        var finishDate = task.Children.Where(c => c.Plan is not null).Max(c => c.Plan!.PlannedFinish);

        task.Plan = new TaskPlanDto
        {
            PlannedStart = startDate,
            PlannedFinish = finishDate
        };
    }

    // Порядок построения модели важен: сначала создаются переменные start/end
    // и целевые слагаемые по приоритетам (на них ссылаются все остальные
    // ограничения), затем зависимости между задачами и рамки по датам, затем
    // недоступность ресурсов, и в конце - интервалы задач с учётом выбора
    // ресурса и его нерабочих дней.
    private void PrepareSolverConstraints()
    {
        CreateStartEndVarsAndPriorityObjectives();
        CreateDependencyBetweenTasks();
        CreateResourceRestrictions();
        CreateTasksIntervals();
    }

    // Для каждой планируемой задачи перебирает всех кандидатов-ресурсов
    // (см. FindResourcesForTask) и строит для каждого из них "опциональный"
    // интервал (задействуется только если солвер выберет именно этот
    // ресурс). Рабочая длительность задачи (в бизнес-днях) переводится в
    // календарную по календарю ресурса (см. CreateOptionalTaskInterval).
    // Для задачи выбирается ровно один ресурс (AddExactlyOne), а для
    // каждого ресурса все его интервалы (включая "перерывы" из
    // CreateResourceRestrictions) не должны пересекаться (AddNoOverlap).
    private void CreateTasksIntervals()
    {
        foreach (var (taskKey, task) in _taskAlignment.FlattenTasksToSolve)
        {
            if (_fixedTasks.TryGetValue(taskKey, out var fixedTask))
            {
                CreateFixedTaskInterval(taskKey, fixedTask);
                continue;
            }

            var useList = new List<ILiteral>();
            foreach (var resource in FindResourcesForTask(task))
            {
                var use = CreateOptionalTaskInterval(taskKey, task, resource);
                if (use is not null)
                {
                    useList.Add(use);
                }
            }

            if (useList.Count == 0)
            {
                throw new HorizonExceededException(
                    $"Task {taskKey} doesn't fit into the planning horizon ({Horizon} days) for any resource");
            }

            _model.AddExactlyOne(useList);
        }

        foreach (var intervals in _personIntervals.Values)
        {
            _model.AddNoOverlap(intervals);
        }
    }

    // Задача в работе: старт, исполнитель и остаток известны (см.
    // PrepareFixedTasks), так что и конец - константа.
    private void CreateFixedTaskInterval(string taskKey, FixedTask fixedTask)
    {
        var resource = fixedTask.Resource;
        var end = CalcTaskEnd(resource, fixedTask.Start, fixedTask.Duration);
        if (end is null || end > Horizon)
        {
            throw new HorizonExceededException(
                $"Task {taskKey} is in progress and doesn't fit into the planning horizon ({Horizon} days)");
        }

        _model.Add(_ends[taskKey] == end.Value);
        _personIntervals[resource.Name].Add(
            _model.NewIntervalVar(fixedTask.Start, end.Value - fixedTask.Start, end.Value, $"fix_{taskKey}_{resource.Name}"));
    }

    // Длительность задачи у конкретного ресурса фиксирована, поэтому для
    // каждого возможного старта s конец задачи по календарю ресурса
    // известен заранее: endTable[s] (см. CalcTaskEnd - туда же входит и
    // правило закрытия). Связь start -> end задаётся одним AddElement
    // вместо поштучного учёта каждого нерабочего дня. Старт в нерабочий
    // день, раньше границы истории исполнителя (см. CalcHistoryEnd) или
    // такой, что задача не влезает в горизонт, помечен в таблице как NoEnd
    // и запрещён, если выбран этот ресурс, - иначе солвер "выгодно"
    // начинал бы задачи в субботу: конец тот же, а start в целевой функции
    // меньше. Последний день задачи по построению тоже всегда рабочий.
    // Возвращает литерал "задачу делает этот ресурс" или null, если у
    // ресурса нет ни одного допустимого старта.
    private BoolVar? CreateOptionalTaskInterval(string taskKey, TaskDto task, ResourceDto resource)
    {
        var noEnd = Horizon + 1;

        var duration = CalcTaskLimitDuration(task, resource);
        var calendar = _calendars[resource.Name];
        var historyEnd = _historyEnd.GetValueOrDefault(resource.Name);

        // Индекс таблицы - значение start, его домен [0, Horizon].
        var endTable = new long[Horizon + 1];
        Array.Fill(endTable, noEnd);
        for (var s = Math.Max(0, historyEnd); s < Horizon; s++)
        {
            if (calendar.IsWorkingDay(s) && CalcTaskEnd(resource, s, duration) is { } end && end <= Horizon)
            {
                endTable[s] = end;
            }
        }

        var sizes = Enumerable.Range(0, Horizon)
            .Where(s => endTable[s] != noEnd)
            .Select(s => endTable[s] - s)
            .ToList();

        if (sizes.Count == 0)
        {
            return null;
        }

        var use = _model.NewBoolVar($"use_{taskKey}_{resource.Name}");
        _personUses[(taskKey, resource.Name)] = use;

        // AddElement действует безусловно (таблица определена для любого
        // start), а вот запрет NoEnd и связь с общим end задачи - только
        // если задачу делает именно этот ресурс.
        var resourceEnd = _model.NewIntVarFromDomain(
            Domain.FromValues(endTable.Distinct().ToArray()), $"end_{taskKey}_{resource.Name}");
        _model.AddElement(_starts[taskKey], endTable, resourceEnd);
        _model.Add(resourceEnd != noEnd).OnlyEnforceIf(use);
        _model.Add(_ends[taskKey] == resourceEnd).OnlyEnforceIf(use);

        var size = _model.NewIntVar(sizes.Min(), sizes.Max(), $"size_{taskKey}_{resource.Name}");
        _personIntervals[resource.Name].Add(
            _model.NewOptionalIntervalVar(_starts[taskKey], size, _ends[taskKey], use, $"opt_{taskKey}_{resource.Name}"));

        return use;
    }

    // Фиктивные "занятые" интервалы на периоды, когда ресурс вообще
    // недоступен на проекте (AvailFrom/AvailTo). Эти интервалы складываются
    // в _personIntervals вместе с интервалами задач, поэтому общий
    // AddNoOverlap в CreateTasksIntervals автоматически не даёт солверу
    // назначить задачу на период недоступности ресурса.
    private void CreateResourceRestrictions()
    {
        _personIntervals.Clear();

        foreach (var resource in _project.Resources)
        {
            _personIntervals[resource.Name] = [];

            // Ресурс недоступен до AvailFrom - "занимаем" его фиктивным
            // интервалом с начала горизонта до AvailFrom.
            if (resource.AvailFrom > _project.ProjectStart)
            {
                var dateShift = Offset(resource.AvailFrom.Value);
                var breakInterval = _model.NewIntervalVar(0, dateShift, dateShift, $"brk_from_{resource.Name}");
                _personIntervals[resource.Name].Add(breakInterval);
            }

            // Ресурс недоступен после AvailTo - "занимаем" его до конца
            // горизонта. AvailTo - последний доступный день включительно
            // (так же его понимают DailyPlanExporter/QuarterDistributionExporter),
            // поэтому занято со следующего дня.
            if (resource.AvailTo.HasValue)
            {
                var from = Math.Max(0, Offset(resource.AvailTo.Value) + 1);

                var dur = Horizon - from;
                if (dur > 0)
                {
                    var breakInterval = _model.NewIntervalVar(from, dur, Horizon, $"brk_to_{resource.Name}");
                    _personIntervals[resource.Name].Add(breakInterval);
                }
            }
        }
    }

    // Жёсткие ограничения по датам: задача не может начаться раньше, чем
    // закончатся все её незавершённые предшественники (FS-зависимость), а
    // также должна укладываться в DueDate/StartAfter, если они заданы.
    private void CreateDependencyBetweenTasks()
    {
        _dueDateWarnings.Clear();

        foreach (var (taskKey, task) in _taskAlignment.FlattenTasksToSolve)
        {
            // Старт уже начатой задачи - константа из факта (см.
            // PrepareFixedTasks): что было, то было, предшественники и
            // StartAfter на него уже не влияют. DueDate по-прежнему
            // действует - он ограничивает конец.
            var fixedTask = _fixedTasks.GetValueOrDefault(taskKey);

            if (task.Limit!.DueDate is { } dueDate)
            {
                AddDueDate(taskKey, task, dueDate, fixedTask);
            }

            if (fixedTask is not null)
            {
                continue;
            }

            foreach (var predecessorId in task.Limit.PredecessorIds)
            {
                // PredecessorResolver уже вырезал из PredecessorIds всех
                // Disabled/Paused/Finished предшественников (см.
                // TaskAlignment/PredecessorResolver.cs) - у любого id,
                // дошедшего сюда, гарантированно есть start/end в модели.
                Debug.Assert(!_taskAlignment.FlattenTasksCopy[predecessorId].CanSkipTask,
                    $"Predecessor {predecessorId} should have been filtered out by PredecessorResolver");

                _model.Add(_starts[taskKey] >= _ends[predecessorId]);
            }

            var minStart = CalcMinStart(task);
            if (minStart > 0)
            {
                _model.Add(_starts[taskKey] >= minStart);
            }
        }
    }

    // DueDate, который уже заведомо не выполнить, не делает весь план
    // невыполнимым - ограничение пропускается с предупреждением. Так бывает,
    // если задача уже в работе и её остаток не успевает к сроку, или если
    // срок раньше дня, до которого незакрытая задача в любом случае тянется
    // (см. CalcTaskEnd).
    // DueDate - последний допустимый день включительно (как и AvailTo): end
    // - день, следующий за последним днём задачи, поэтому end <= DueDate + 1.
    private void AddDueDate(string taskKey, TaskDto task, DateOnly dueDate, FixedTask? fixedTask)
    {
        var endLimit = Offset(dueDate) + 1;

        int? earliestEnd = null;
        if (fixedTask is not null)
        {
            earliestEnd = CalcTaskEnd(fixedTask.Resource, fixedTask.Start, fixedTask.Duration);
        }
        else if (_factOffset is { } factOffset)
        {
            earliestEnd = FindResourcesForTask(task)
                .Select(r => _calendars[r.Name].CalcEndOffset(factOffset + 1, 1))
                .Min();
        }

        if (earliestEnd > endLimit)
        {
            _dueDateWarnings.Add($"Задача {taskKey}: DueDate {dueDate:dd.MM.yyyy} уже не достижим " +
                          $"(закончится не раньше {ToDate(earliestEnd.Value - 1):dd.MM.yyyy}) - ограничение пропущено");
            return;
        }

        _model.Add(_ends[taskKey] <= endLimit);
    }

    // Самый ранний допустимый старт ещё не начатой задачи без учёта
    // предшественников и занятости исполнителя: StartAfter.
    private int CalcMinStart(TaskDto task)
    {
        return task.Limit!.StartAfter is { } startAfter ? Offset(startAfter) : 0;
    }

    // Создаёт по паре переменных start/end (в днях от ProjectStart, в
    // пределах горизонта) на каждую планируемую задачу - на них дальше
    // ссылаются все остальные ограничения. У уже начатой задачи start -
    // константа из факта (см. PrepareFixedTasks). Заодно формирует слагаемое
    // целевой функции "start * вес приоритета": чем важнее задача, тем
    // дороже обходится солверу сдвигать её начало вправо.
    private void CreateStartEndVarsAndPriorityObjectives()
    {
        _starts.Clear();
        _ends.Clear();
        _personUses.Clear();
        _priorityObjectives.Clear();

        foreach (var (taskKey, task) in _taskAlignment.FlattenTasksToSolve)
        {
            var startIntVar = _fixedTasks.TryGetValue(taskKey, out var fixedTask)
                ? _model.NewConstant(fixedTask.Start)
                : _model.NewIntVar(0, Horizon, $"start_{taskKey}");

            var endIntVar = _model.NewIntVar(0, Horizon, $"end_{taskKey}");
            _starts[taskKey] = startIntVar;
            _ends[taskKey] = endIntVar;

            var po = startIntVar * _weights[task.Limit!.Priority ?? 0];
            _priorityObjectives.Add(po);
        }
    }

    // Последняя запись прогресса (Started/InProgress/CorrectedDuration),
    // предпочтительно с известным остатком Duration. Если ни у одной записи
    // остатка нет (например, Started без Duration) - просто последняя
    // запись прогресса, а остаток тогда берётся из лимитов задачи.
    // Вызывается только для задач, у которых есть хотя бы одна запись
    // Started/InProgress.
    private static TaskFactRecordDto FindLastProgressRecord(TaskFactDto fact)
    {
        static bool IsProgressRecord(TaskFactRecordDto x) =>
            x.Type is TaskFactRecordType.Started or TaskFactRecordType.InProgress
                or TaskFactRecordType.CorrectedDuration;

        return fact.Records.LastOrDefault(x => IsProgressRecord(x) && x.Duration is not null)
               ?? fact.Records.Last(IsProgressRecord);
    }

    // Рабочая (бизнес-дневная) длительность задачи для конкретного ресурса:
    // явно заданная Duration или оценка по TShirt-размеру с поправкой на
    // уверенность оценки ресурса (Confidence: 100% - нижняя граница, 0% -
    // верхняя), затем прибавляется буфер, затем длительность увеличивается
    // пропорционально неполной загрузке ресурса на задаче (Percent) с
    // округлением вверх: неполный день - всё равно занятый день (5 дней при
    // 80% - это 6.25, то есть 7, а не 6).
    private int CalcTaskLimitDuration(TaskDto task, ResourceDto resource)
    {
        var duration = task.Limit!.Duration ?? task.Limit.TShirt!.Value.ToDays(resource.Confidence);

        if (task.Limit.Buffer is not null)
        {
            duration += task.Limit.Buffer.Value;
        }

        if (resource.Percent != 100)
        {
            duration = Math.Max(1, Convert.ToInt32(Math.Ceiling(duration * 100.0 / resource.Percent)));
        }

        return duration;
    }

    // Определяет список кандидатов-ресурсов, среди которых солвер будет
    // выбирать исполнителя задачи (CreateTasksIntervals). Приоритет:
    // 1) если задача уже в работе - только тот, кто её реально делал
    //    последний раз (менять исполнителя нельзя; сменить можно записью
    //    факта с другим ResourceName, например ChangedAssignee);
    // 2) иначе, если в лимитах явно указан ResourceName - только он;
    // 3) иначе - все ресурсы с подходящей ResourceRole (солвер сам выберет
    //    из них наиболее подходящего по календарю/загрузке).
    private List<ResourceDto> FindResourcesForTask(TaskDto task)
    {
        List<ResourceDto> resources;
        var resourceName = task.Limit!.ResourceName;

        if (task.Fact is not null && task.Fact.IsProgress)
        {
            // trying to find, who actually do this task last time
            var lastAssigneeRec = task.Fact.Records.LastOrDefault(x => x.ResourceName is not null);
            if (lastAssigneeRec is not null)
            {
                resourceName = lastAssigneeRec.ResourceName;
            }
        }

        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            var resource = _project.Resources.SingleOrDefault(r => r.Name == resourceName);
            if (resource is null)
            {
                throw new Exception($"Can't find resource {resourceName} for task {task.Id}");
            }

            resources = [resource];
        }
        else
        {
            // all resources for this role
            resources = _project.Resources
                .Where(r => r.Role == task.Limit!.ResourceRole)
                .ToList();
        }

        return resources;
    }
}
