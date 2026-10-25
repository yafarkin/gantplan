using System.Diagnostics;
using GantPlan.Dtos;
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
// Части решения разнесены по классам: общее состояние и расчёты - в
// PlanningContext, план и факт (авто-прогресс, закрытие, граница истории)
// - в FactProgress, жадный план для горизонта и подсказки - в
// GreedyPlanner. Здесь - сама модель, её решение и разбор результата.
public sealed class Solver
{
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

    private PlanningContext _context = null!;
    private FactProgress _facts = null!;

    private CpModel _model = null!;
    private readonly Dictionary<string, IntVar> _starts = new();
    private readonly Dictionary<string, IntVar> _ends = new();
    private readonly Dictionary<(string task, string person), BoolVar> _personUses = new();
    private readonly Dictionary<string, List<IntervalVar>> _personIntervals = new();
    private readonly List<LinearExpr> _priorityObjectives = new();

    // Предупреждения о входных данных (копятся при подготовке) и о
    // DueDate (пересчитываются при каждой перестройке модели, см.
    // CreateDependencyBetweenTasks) - отдельно, чтобы перестройка при
    // нехватке горизонта не теряла первые и не дублировала вторые.
    private readonly List<string> _warnings = new();
    private readonly List<string> _dueDateWarnings = new();
    private readonly List<TaskDto> _awaitingClosure = new();

    // См. GreedyPlanner.
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
        TieBreakStatus = null;
        _warnings.Clear();
        _dueDateWarnings.Clear();
        _awaitingClosure.Clear();

        FactProgress.RemoveAutoFacts(project.RootTask);

        // Разворачивает дерево задач проекта в плоский список задач,
        // которые нужно планировать (с учётом приоритетов/весов).
        var alignment = new TaskAlignment();
        alignment.Alignment(project, _weights);

        _context = new PlanningContext(project, alignment, _weights);
        _facts = new FactProgress(_context);
        _facts.CalcHistoryEnd(_warnings);
        _facts.PrepareFixedTasks();

        // Горизонт - по жадному плану с запасом (см. CalcHorizon). Если
        // какая-то задача всё же в него не влезла - удваиваем и строим
        // модель заново.
        _greedyPlan = GreedyPlanner.Build(_context);
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
            catch (HorizonExceededException) when (Horizon < PlanningContext.MaxHorizon)
            {
                Horizon = Math.Min(PlanningContext.MaxHorizon, Horizon * 2);
            }
        }

        // Цель солвера - минимизировать общий срок проекта (makespan, то есть
        // максимальный end среди всех планируемых задач) плюс сумму
        // приоритетных штрафов за поздний старт (см. _weights).
        var makespan = _model.NewIntVar(0, Horizon, "makespan");
        _model.AddMaxEquality(makespan, alignment.FlattenTasksToSolve.Select(t => _ends[t.Key]).ToArray());
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

        var tasks = _context.Alignment.FlattenTasksToSolve.Keys.ToList();
        var resourceIndex = _context.Project.Resources
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
        var resourceTermMax = (long)tasks.Count * tasks.Count * _context.Project.Resources.Count + 1;

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

    // Горизонт = срок жадного плана с запасом: оптимальный план обычно
    // короче жадного, но может и чуть выйти за него (например, задачу без
    // приоритета выгодно отодвинуть подальше, чтобы приоритетная начала
    // раньше). Слишком щедрый горизонт не бесплатен - модель растёт
    // пропорционально ему, и солвер ищет план заметно дольше.
    private int CalcHorizon()
    {
        if (_greedyPlan is null)
        {
            return PlanningContext.MaxHorizon;
        }

        var makespan = _greedyPlan.Values.Select(p => p.End).DefaultIfEmpty(0).Max();

        return Math.Clamp(makespan + Math.Max(14, makespan / 4), 1, PlanningContext.MaxHorizon);
    }

    // Подсказка солверу - жадный план (см. GreedyPlanner). Если он нарушает
    // DueDate, солвер просто отбросит подсказку и будет искать сам.
    private void AddGreedyHints()
    {
        if (_greedyPlan is null)
        {
            return;
        }

        foreach (var (taskKey, (start, _, resource)) in _greedyPlan)
        {
            if (_context.FixedTasks.ContainsKey(taskKey))
            {
                continue;
            }

            _model.AddHint(_starts[taskKey], start);
            foreach (var person in _context.Project.Resources)
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
    // FactProgress.AddAutoFacts). Plan у планируемых задач всегда
    // перезаписывается - это результат расчёта, а не входные данные.
    private void PostFillAfterSolve(CpSolver solver)
    {
        foreach (var (key, task) in _context.Alignment.OriginalTasks)
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
            var fixedTask = _context.FixedTasks.GetValueOrDefault(key);
            var resource = fixedTask?.Resource
                           ?? _context.Project.Resources.FirstOrDefault(r =>
                               _personUses.TryGetValue((key, r.Name), out var use) && solver.Value(use) == 1)
                           ?? throw new Exception($"Can't find resource for task {key}");

            var duration = fixedTask?.Duration ?? _context.CalcTaskLimitDuration(task, resource);

            // Конец "по рабочим дням" раньше фактического - значит, задачу
            // дотянуло правило закрытия (см. PlanningContext.CalcTaskEnd).
            var awaitingClosure = end > _context.Calendars[resource.Name].CalcEndOffset(start, duration);

            task.Plan = new TaskPlanDto
            {
                ResourceName = resource.Name,
                PlannedStart = _context.ToDate(start),
                // end - день, следующий за последним днём задачи.
                PlannedFinish = _context.ToDate(end - 1),
                AwaitingClosure = awaitingClosure
            };

            if (awaitingClosure)
            {
                _awaitingClosure.Add(task);
            }

            _facts.AddAutoFacts(task, start, end, duration, resource, isStarted: fixedTask is not null);
        }

        _facts.FillFinishedTasksPlan();
        PostFillContainerTasks(_context.Project.RootTask);
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
    // (см. PlanningContext.FindResourcesForTask) и строит для каждого из них
    // "опциональный" интервал (задействуется только если солвер выберет
    // именно этот ресурс). Рабочая длительность задачи (в бизнес-днях)
    // переводится в календарную по календарю ресурса (см.
    // CreateOptionalTaskInterval). Для задачи выбирается ровно один ресурс
    // (AddExactlyOne), а для каждого ресурса все его интервалы (включая
    // "перерывы" из CreateResourceRestrictions) не должны пересекаться
    // (AddNoOverlap).
    private void CreateTasksIntervals()
    {
        foreach (var (taskKey, task) in _context.Alignment.FlattenTasksToSolve)
        {
            if (_context.FixedTasks.TryGetValue(taskKey, out var fixedTask))
            {
                CreateFixedTaskInterval(taskKey, fixedTask);
                continue;
            }

            var useList = new List<ILiteral>();
            foreach (var resource in _context.FindResourcesForTask(task))
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
    // FactProgress.PrepareFixedTasks), так что и конец - константа.
    private void CreateFixedTaskInterval(string taskKey, FixedTask fixedTask)
    {
        var resource = fixedTask.Resource;
        var end = _context.CalcTaskEnd(resource, fixedTask.Start, fixedTask.Duration);
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
    // известен заранее: endTable[s] (см. PlanningContext.CalcTaskEnd - туда
    // же входит и правило закрытия). Связь start -> end задаётся одним
    // AddElement вместо поштучного учёта каждого нерабочего дня. Старт в
    // нерабочий день, раньше границы истории исполнителя (см.
    // FactProgress.CalcHistoryEnd) или такой, что задача не влезает в
    // горизонт, помечен в таблице как NoEnd и запрещён, если выбран этот
    // ресурс, - иначе солвер "выгодно" начинал бы задачи в субботу: конец
    // тот же, а start в целевой функции меньше. Последний день задачи по
    // построению тоже всегда рабочий.
    // Возвращает литерал "задачу делает этот ресурс" или null, если у
    // ресурса нет ни одного допустимого старта.
    private BoolVar? CreateOptionalTaskInterval(string taskKey, TaskDto task, ResourceDto resource)
    {
        var noEnd = Horizon + 1;

        var duration = _context.CalcTaskLimitDuration(task, resource);
        var calendar = _context.Calendars[resource.Name];
        var historyEnd = _context.HistoryEnd.GetValueOrDefault(resource.Name);

        // Индекс таблицы - значение start, его домен [0, Horizon].
        var endTable = new long[Horizon + 1];
        Array.Fill(endTable, noEnd);
        for (var s = Math.Max(0, historyEnd); s < Horizon; s++)
        {
            if (calendar.IsWorkingDay(s) && _context.CalcTaskEnd(resource, s, duration) is { } end && end <= Horizon)
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

        foreach (var resource in _context.Project.Resources)
        {
            _personIntervals[resource.Name] = [];

            // Ресурс недоступен до AvailFrom - "занимаем" его фиктивным
            // интервалом с начала горизонта до AvailFrom.
            if (resource.AvailFrom > _context.Project.ProjectStart)
            {
                var dateShift = _context.Offset(resource.AvailFrom.Value);
                var breakInterval = _model.NewIntervalVar(0, dateShift, dateShift, $"brk_from_{resource.Name}");
                _personIntervals[resource.Name].Add(breakInterval);
            }

            // Ресурс недоступен после AvailTo - "занимаем" его до конца
            // горизонта. AvailTo - последний доступный день включительно
            // (так же его понимают DailyPlanExporter/QuarterDistributionExporter),
            // поэтому занято со следующего дня.
            if (resource.AvailTo.HasValue)
            {
                var from = Math.Max(0, _context.Offset(resource.AvailTo.Value) + 1);

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

        foreach (var (taskKey, task) in _context.Alignment.FlattenTasksToSolve)
        {
            // Старт уже начатой задачи - константа из факта (см.
            // FactProgress.PrepareFixedTasks): что было, то было,
            // предшественники и StartAfter на него уже не влияют. DueDate
            // по-прежнему действует - он ограничивает конец.
            var fixedTask = _context.FixedTasks.GetValueOrDefault(taskKey);

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
                Debug.Assert(!_context.Alignment.FlattenTasksCopy[predecessorId].CanSkipTask,
                    $"Predecessor {predecessorId} should have been filtered out by PredecessorResolver");

                _model.Add(_starts[taskKey] >= _ends[predecessorId]);
            }

            var minStart = _context.CalcMinStart(task);
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
    // (см. PlanningContext.CalcTaskEnd).
    // DueDate - последний допустимый день включительно (как и AvailTo): end
    // - день, следующий за последним днём задачи, поэтому end <= DueDate + 1.
    private void AddDueDate(string taskKey, TaskDto task, DateOnly dueDate, FixedTask? fixedTask)
    {
        var endLimit = _context.Offset(dueDate) + 1;

        int? earliestEnd = null;
        if (fixedTask is not null)
        {
            earliestEnd = _context.CalcTaskEnd(fixedTask.Resource, fixedTask.Start, fixedTask.Duration);
        }
        else if (_context.FactOffset is { } factOffset)
        {
            earliestEnd = _context.FindResourcesForTask(task)
                .Select(r => _context.Calendars[r.Name].CalcEndOffset(factOffset + 1, 1))
                .Min();
        }

        if (earliestEnd > endLimit)
        {
            _dueDateWarnings.Add($"Задача {taskKey}: DueDate {dueDate:dd.MM.yyyy} уже не достижим " +
                                 $"(закончится не раньше {_context.ToDate(earliestEnd.Value - 1):dd.MM.yyyy}) - ограничение пропущено");
            return;
        }

        _model.Add(_ends[taskKey] <= endLimit);
    }

    // Создаёт по паре переменных start/end (в днях от ProjectStart, в
    // пределах горизонта) на каждую планируемую задачу - на них дальше
    // ссылаются все остальные ограничения. У уже начатой задачи start -
    // константа из факта (см. FactProgress.PrepareFixedTasks). Заодно
    // формирует слагаемое целевой функции "start * вес приоритета": чем
    // важнее задача, тем дороже обходится солверу сдвигать её начало вправо.
    private void CreateStartEndVarsAndPriorityObjectives()
    {
        _starts.Clear();
        _ends.Clear();
        _personUses.Clear();
        _priorityObjectives.Clear();

        foreach (var (taskKey, task) in _context.Alignment.FlattenTasksToSolve)
        {
            var startIntVar = _context.FixedTasks.TryGetValue(taskKey, out var fixedTask)
                ? _model.NewConstant(fixedTask.Start)
                : _model.NewIntVar(0, Horizon, $"start_{taskKey}");

            var endIntVar = _model.NewIntVar(0, Horizon, $"end_{taskKey}");
            _starts[taskKey] = startIntVar;
            _ends[taskKey] = endIntVar;

            var po = startIntVar * _weights[task.Limit!.Priority ?? 0];
            _priorityObjectives.Add(po);
        }
    }
}
