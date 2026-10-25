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
    // Зафиксированный фактом старт уже начатых задач (см. CalcFixedStart).
    private readonly Dictionary<string, int> _fixedStarts = new();
    private readonly Dictionary<(string task, string person), BoolVar> _personUses = new();
    private readonly Dictionary<string, List<IntervalVar>> _personIntervals = new();
    private readonly List<LinearExpr> _priorityObjectives = new();
    
    private readonly Dictionary<string, CalendarLogic>  _calendars = new();

    // См. CalcMinStart.
    private bool _hasAlreadyCompletedWork;

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

    public bool Solve(ProjectDto project, int? maxSeconds = null)
    {
        _project = project;
        TieBreakStatus = null;

        // Разворачивает дерево задач проекта в плоский список задач,
        // которые нужно планировать (с учётом приоритетов/весов).
        _taskAlignment = new TaskAlignment();
        _taskAlignment.Alignment(_project, _weights);

        // Горизонт - по жадному плану с запасом (см. CalcHorizon). Если
        // какая-то задача всё же в него не влезла - удваиваем и строим
        // модель заново.
        _hasAlreadyCompletedWork = _taskAlignment.OriginalTasks.Values.Any(t => t.Fact?.IsFinished == true);
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
        var calendars = _project.Resources.ToDictionary(
            r => r.Name,
            r => new CalendarLogic(MaxHorizon, _project.ProjectStart, _project.GlobalCalendar, r.Calendar));

        // Занятость исполнителя - как в CreateResourceRestrictions: до
        // AvailFrom и после AvailTo он недоступен.
        var busy = _project.Resources.ToDictionary(r => r.Name, r =>
        {
            var intervals = new List<(int Start, int End)>();
            if (r.AvailFrom is not null)
            {
                intervals.Add((int.MinValue, r.AvailFrom.Value.DayNumber - _project.ProjectStart.DayNumber));
            }

            if (r.AvailTo is not null)
            {
                intervals.Add((r.AvailTo.Value.DayNumber + 1 - _project.ProjectStart.DayNumber, int.MaxValue));
            }

            return intervals;
        });

        var plan = new Dictionary<string, (int Start, int End, string Resource)>();
        var tasks = _taskAlignment.FlattenTasksToSolve;

        // Уже начатые задачи стоят там, где стоят.
        foreach (var (taskKey, task) in tasks)
        {
            if (task.Fact?.IsProgress != true)
            {
                continue;
            }

            var resource = FindResourcesForTask(task).Single();
            var start = CalcFixedStart(task.Fact);
            var duration = FindLastProgressRecord(task.Fact).Duration ?? CalcTaskLimitDuration(task, resource);
            if (calendars[resource.Name].CalcEndOffset(start, duration) is not { } end)
            {
                return null;
            }

            plan[taskKey] = (start, end, resource.Name);
            busy[resource.Name].Add((start, end));
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
                var slot = FindFreeSlot(calendars[resource.Name], busy[resource.Name], earliest,
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

    // Первое окно не раньше earliest: старт в рабочий день, duration рабочих
    // дней по календарю, без пересечения с занятостью исполнителя.
    private static (int Start, int End)? FindFreeSlot(
        CalendarLogic calendar, List<(int Start, int End)> busy, int earliest, int duration)
    {
        var start = earliest;
        while (start < MaxHorizon)
        {
            if (!calendar.IsWorkingDay(start))
            {
                start++;
                continue;
            }

            if (calendar.CalcEndOffset(start, duration) is not { } end)
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
            if (_fixedStarts.ContainsKey(taskKey))
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
    // переводит целочисленные start/end в даты, определяет фактического
    // исполнителя каждой задачи и, если задача ещё не завершена, дописывает
    // в неё записи факта (Started / InProgress / Completed) на основе
    // FactDate проекта (дата среза "на сегодня").
    private void PostFillAfterSolve(CpSolver solver)
    {
        var projectStart = _project.ProjectStart;

        foreach (var kv in _taskAlignment.OriginalTasks)
        {
            var key = kv.Key;
            var task = kv.Value;

            if (!_starts.ContainsKey(key))
            {
                continue;
            }

            var sd = (int)solver.Value(_starts[key]);
            var ed = (int)solver.Value(_ends[key]);
            var calcedStart = projectStart.AddDays(sd);
            // end хранится как "день, следующий за последним рабочим днём"
            // задачи, поэтому дата окончания - это (end - 1) день.
            var calcedEnd = projectStart.AddDays(ed - 1);

            task.Fact ??= new TaskFactDto();
            task.Plan ??= new TaskPlanDto
            {
                PlannedStart = calcedStart
            };

            task.Plan.PlannedFinish = calcedEnd;

            // Определяем исполнителя задачи: если задача уже в работе - это тот,
            // кто её реально делал последний раз (менять исполнителя "задним
            // числом" нельзя); иначе - тот ресурс, для которого солвер выставил
            // use-переменную в 1 (см. CreateOptionalTaskInterval).
            ResourceDto? resource = null;
            if (task.Fact!.IsProgress)
            {
                var lastAssigneeRec = task.Fact.Records.Last(x => x.ResourceName != null);
                resource = _project.Resources.Single(r => r.Name == lastAssigneeRec.ResourceName);
            }
            else
            {
                foreach (var person in _project.Resources)
                {
                    if (!_personUses.ContainsKey((task.Id, person.Name)) ||
                        solver.Value(_personUses[(task.Id, person.Name)]) != 1)
                    {
                        continue;
                    }

                    resource = person;

                    break;
                }
            }

            if (resource is null)
            {
                throw new Exception($"Can't find resource for task {task.Id}");
            }

            if (string.IsNullOrWhiteSpace(task.Plan.ResourceName))
            {
                task.Plan.ResourceName = resource.Name;
            }

            if (task.Fact.IsFinished)
            {
                continue;
            }

            // Задачу ещё вообще не трогали (ни разу не отмечали ни Started, ни
            // InProgress/CorrectedDuration) - фиксируем факт старта с той
            // длительностью, которую заложили в модель для выбранного ресурса.
            // Проверяем именно "нет вообще никакой прогресс-записи", а не узко
            // "нет записи именно типа Started" - иначе если задаче на входе
            // задали только InProgress (например, "уже наполовину сделана", без
            // отдельно заведённого Started), этот код решал бы, что задача
            // "ещё не стартовала", и дописывал бы вторую, конкурирующую запись
            // с длительностью, пересчитанной заново из Limit - молча замещая
            // (при выборе "последней прогресс-записи" ниже и в
            // FindLastProgressRecord) уже известный, реальный остаток чужой,
            // выдуманной оценкой.
            var hasAnyProgressRecord = task.Fact.Records.Any(x =>
                x.Type is TaskFactRecordType.Started or TaskFactRecordType.InProgress
                    or TaskFactRecordType.CorrectedDuration);

            if (!hasAnyProgressRecord)
            {
                task.Fact.Records.Add(new TaskFactRecordDto
                {
                    RecordedAt = calcedStart,
                    ResourceName = resource.Name,
                    Duration = CalcTaskLimitDuration(task, resource),
                    Type = TaskFactRecordType.Started
                });
            }

            // Без даты среза ("сегодня" проекта) дальше сравнивать не с чем -
            // прогресс по факту не считаем.
            if (_project.FactDate is null)
            {
                continue;
            }

            if (_project.FactDate >= calcedEnd)
            {
                // Дата среза уже позже планового окончания - считаем задачу
                // завершённой по факту.
                if (task.Fact.Records.All(x => x.Type != TaskFactRecordType.Completed))
                {
                    task.Fact.Records.Add(new TaskFactRecordDto
                    {
                        RecordedAt = calcedEnd,
                        ResourceName = resource.Name,
                        Type = TaskFactRecordType.Completed
                    });
                }
            }
            else if (_project.FactDate >= calcedStart && _project.FactDate <= calcedEnd)
            {
                // Дата среза попадает внутрь планового интервала задачи -
                // задача в процессе, нужно пересчитать оставшуюся длительность.
                var lastProgressRec = FindLastProgressRecord(task.Fact);

                if (lastProgressRec.RecordedAt < _project.FactDate)
                {
                    var calendar = _calendars[resource.Name];
                    var lastDuration = lastProgressRec.Duration ?? CalcTaskLimitDuration(task, resource);

                    // Оставшаяся длительность = длительность на момент последней
                    // отметки минус число рабочих дней ресурса, фактически
                    // прошедших между стартом и датой среза.
                    var remainingDur = lastDuration - calendar.CalcWorkingDaysCount(calcedStart, _project.FactDate.Value);

                    task.Fact.Records.Add(new TaskFactRecordDto
                    {
                        RecordedAt = _project.FactDate.Value,
                        ResourceName = resource.Name,
                        Duration = remainingDur,
                        Type = TaskFactRecordType.InProgress
                    });
                }
            }
        }

        PostFillContainerTasks(_project.RootTask);
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
    // календари/недоступность ресурсов, и в конце - интервалы задач с учётом
    // выбора ресурса и его нерабочих дней.
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
            var resources = FindResourcesForTask(task);

            if (_fixedStarts.TryGetValue(taskKey, out var fixedStart))
            {
                // Уже начатая задача: исполнитель (единственный кандидат, см.
                // FindResourcesForTask) и старт известны из факта.
                CreateFixedTaskInterval(taskKey, task, fixedStart, resources.Single());
                continue;
            }

            var useList = new List<ILiteral>();
            foreach (var resource in resources)
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

    // Задача в работе: старт и исполнитель зафиксированы фактом, длительность
    // - остаток из последней записи прогресса, так что и конец - константа.
    private void CreateFixedTaskInterval(string taskKey, TaskDto task, int fixedStart, ResourceDto resource)
    {
        var duration = FindLastProgressRecord(task.Fact!).Duration ?? CalcTaskLimitDuration(task, resource);

        var end = _calendars[resource.Name].CalcEndOffset(fixedStart, duration)
                  ?? throw new HorizonExceededException(
                      $"Task {taskKey} is in progress and doesn't fit into the planning horizon ({Horizon} days)");

        _model.Add(_ends[taskKey] == end);
        _personIntervals[resource.Name].Add(
            _model.NewIntervalVar(fixedStart, end - fixedStart, end, $"fix_{taskKey}_{resource.Name}"));
    }

    // Длительность задачи у конкретного ресурса фиксирована, поэтому для
    // каждого возможного старта s конец задачи по календарю ресурса
    // известен заранее: endTable[s]. Связь start -> end задаётся одним
    // AddElement вместо поштучного учёта каждого нерабочего дня. Старт в
    // нерабочий день (или такой, что задача не влезает в горизонт) помечен
    // в таблице как NoEnd и запрещён, если выбран этот ресурс, - иначе
    // солвер "выгодно" начинал бы задачи в субботу: конец тот же, а start
    // в целевой функции меньше. Последний день задачи по построению
    // таблицы тоже всегда рабочий.
    // Возвращает литерал "задачу делает этот ресурс" или null, если у
    // ресурса нет ни одного допустимого старта.
    private BoolVar? CreateOptionalTaskInterval(string taskKey, TaskDto task, ResourceDto resource)
    {
        var noEnd = Horizon + 1;

        var duration = CalcTaskLimitDuration(task, resource);
        var calendar = _calendars[resource.Name];

        // Индекс таблицы - значение start, его домен [0, Horizon].
        var endTable = new long[Horizon + 1];
        Array.Fill(endTable, noEnd);
        for (var s = 0; s < Horizon; s++)
        {
            if (calendar.IsWorkingDay(s) && calendar.CalcEndOffset(s, duration) is { } end)
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

    // Готовит на каждого ресурса: 1) персональный календарь рабочих/нерабочих
    // дней (глобальный календарь проекта + личный календарь ресурса, см.
    // CalendarLogic) и 2) фиктивные "занятые" интервалы на периоды, когда
    // ресурс вообще недоступен на проекте (AvailFrom/AvailTo). Эти интервалы
    // складываются в _personIntervals вместе с интервалами задач, поэтому
    // общий AddNoOverlap в CreateTasksIntervals автоматически не даёт
    // солверу назначить задачу на период недоступности ресурса.
    private void CreateResourceRestrictions()
    {
        _calendars.Clear();
        _personIntervals.Clear();

        foreach (var resource in _project.Resources)
        {
            _personIntervals[resource.Name] = [];

            // Ресурс недоступен до AvailFrom - "занимаем" его фиктивным
            // интервалом с начала горизонта до AvailFrom.
            if (resource.AvailFrom > _project.ProjectStart)
            {
                var dateShift = resource.AvailFrom.Value.DayNumber - _project.ProjectStart.DayNumber;
                var breakInterval = _model.NewIntervalVar(0, dateShift, dateShift, $"brk_from_{resource.Name}");
                _personIntervals[resource.Name].Add(breakInterval);
            }

            // Ресурс недоступен после AvailTo - "занимаем" его до конца
            // горизонта. AvailTo - последний доступный день включительно
            // (так же его понимают DailyPlanExporter/QuarterDistributionExporter),
            // поэтому занято со следующего дня.
            if (resource.AvailTo.HasValue)
            {
                var from = Math.Max(0, resource.AvailTo.Value.DayNumber + 1 - _project.ProjectStart.DayNumber);

                var dur = Horizon - from;
                if (dur > 0)
                {
                    var breakInterval = _model.NewIntervalVar(from, dur, Horizon, $"brk_to_{resource.Name}");
                    _personIntervals[resource.Name].Add(breakInterval);
                }
            }

            var calendar = new CalendarLogic(Horizon, _project.ProjectStart, _project.GlobalCalendar, resource.Calendar);
            _calendars.Add(resource.Name, calendar);
        }
    }

    // Жёсткие ограничения по датам: задача не может начаться раньше, чем
    // закончатся все её незавершённые предшественники (FS-зависимость), а
    // также должна укладываться в DueDate/StartAfter, если они заданы.
    private void CreateDependencyBetweenTasks()
    {
        foreach (var taskKv in _taskAlignment.FlattenTasksToSolve)
        {
            var task = taskKv.Value;

            // Старт уже начатой задачи - константа из факта (см.
            // CreateStartEndVarsAndPriorityObjectives): что было, то было,
            // предшественники и StartAfter на него уже не влияют. DueDate
            // по-прежнему действует - он ограничивает конец.
            var startIsFixed = _fixedStarts.ContainsKey(task.Id);

            if (task.Limit!.DueDate is not null)
            {
                var dueDate = task.Limit.DueDate.Value.DayNumber - _project.ProjectStart.DayNumber;
                _model.Add(_ends[task.Id] <= dueDate);
            }

            if (startIsFixed)
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

                _model.Add(_starts[task.Id] >= _ends[predecessorId]);
            }

            var minStart = CalcMinStart(task);
            if (minStart > 0)
            {
                _model.Add(_starts[task.Id] >= minStart);
            }
        }
    }

    // Самый ранний допустимый старт ещё не начатой задачи без учёта
    // предшественников: StartAfter и защита от бэкдейтинга по FactDate.
    private int CalcMinStart(TaskDto task)
    {
        var minStart = 0;

        if (task.Limit!.StartAfter is not null)
        {
            minStart = task.Limit.StartAfter.Value.DayNumber - _project.ProjectStart.DayNumber;
        }

        // Задача без факта прогресса (ещё по-настоящему не начата) не
        // может стартовать раньше даты среза - иначе задачи, уже ставшие
        // Completed по предыдущей FactDate и переставшие быть интервалом
        // в модели (см. CanSkipTask), оставляют в календарном прошлом
        // "свободное" на вид время, куда солвер может без всякого смысла
        // поставить совершенно новую задачу, которую физически
        // невозможно было начать раньше, чем её вообще добавили в план.
        // Уже начатые задачи сюда не попадают (их старт - константа) -
        // дата их факта вполне законно может быть раньше FactDate.
        //
        // Правило действует, только если в проекте есть хоть одна уже
        // завершённая задача - т.е. мы точно не первый раз решаем этот
        // проект с чистого листа, а перепланируем то, что уже частично
        // прожито. На самом первом решении "с нуля" бэкдейтинг легитимен
        // (например, заводим в систему уже идущий проект и просим солвер
        // восстановить, что по датам должно было случиться раньше FactDate).
        if (_hasAlreadyCompletedWork && _project.FactDate is not null)
        {
            minStart = Math.Max(minStart, _project.FactDate.Value.DayNumber - _project.ProjectStart.DayNumber);
        }

        return minStart;
    }

    // Создаёт по паре переменных start/end (в днях от ProjectStart, в
    // пределах горизонта) на каждую планируемую задачу - на них дальше
    // ссылаются все остальные ограничения. У уже начатой задачи start -
    // константа из факта (см. CalcFixedStart). Заодно формирует слагаемое
    // целевой функции "start * вес приоритета": чем важнее задача, тем
    // дороже обходится солверу сдвигать её начало вправо.
    private void CreateStartEndVarsAndPriorityObjectives()
    {
        _starts.Clear();
        _ends.Clear();
        _fixedStarts.Clear();
        _personUses.Clear();
        _priorityObjectives.Clear();

        foreach (var taskKv in _taskAlignment.FlattenTasksToSolve)
        {
            var task = taskKv.Value;

            IntVar startIntVar;
            if (task.Fact?.IsProgress == true)
            {
                var fixedStart = CalcFixedStart(task.Fact);
                _fixedStarts[task.Id] = fixedStart;
                startIntVar = _model.NewConstant(fixedStart);
            }
            else
            {
                startIntVar = _model.NewIntVar(0, Horizon, $"start_{task.Id}");
            }

            var endIntVar = _model.NewIntVar(0, Horizon, $"end_{task.Id}");
            _starts[task.Id] = startIntVar;
            _ends[task.Id] = endIntVar;

            var po = startIntVar * _weights[task.Limit!.Priority ?? 0];
            _priorityObjectives.Add(po);
        }
    }

    // Старт уже начатой задачи (в днях от ProjectStart) - дата последней
    // записи прогресса. Started - работа в этот день только начата; прочие
    // записи - день уже отработан, остаток считается со следующего дня.
    private int CalcFixedStart(TaskFactDto fact)
    {
        var lastProgressRec = FindLastProgressRecord(fact);
        var fixedStart = lastProgressRec.RecordedAt.DayNumber - _project.ProjectStart.DayNumber;

        return lastProgressRec.Type == TaskFactRecordType.Started ? fixedStart : fixedStart + 1;
    }

    // Последняя запись прогресса (Started/InProgress/CorrectedDuration),
    // предпочтительно с известным остатком Duration. Если ни у одной записи
    // остатка нет (например, Started без Duration) - просто последняя
    // запись прогресса, а остаток тогда берётся из лимитов задачи.
    // Вызывается только для задач с Fact.IsProgress, так что хотя бы
    // одна запись Started/InProgress гарантированно есть.
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
    //    последний раз (менять исполнителя нельзя);
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