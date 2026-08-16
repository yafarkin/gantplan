using System.Diagnostics;
using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using Google.OrTools.Sat;
using LinearExpr = Google.OrTools.Sat.LinearExpr;

namespace GantPlan.Logic;

// Строит и решает CP-SAT модель (Google OR-Tools) распределения задач по
// ресурсам с учётом календарей (выходные/отпуска), зависимостей между
// задачами, ограничений по датам и приоритетов. Результат - расчётные
// start/end каждой задачи в днях от начала проекта, переведённые обратно
// в даты и разложенные по ресурсам.
public sealed class Solver
{
    // Горизонт планирования в днях от ProjectStart - все start/end переменные
    // солвера ограничены диапазоном [0, Horizon).
    private const int Horizon = 180;

    private ProjectDto _project = null!;

    private TaskAlignment _taskAlignment = null!;

    // Веса приоритетов для целевой функции: чем выше приоритет задачи, тем
    // сильнее штраф за поздний старт (см. CreateIntervalsAndPriorityObjectives),
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
    private readonly Dictionary<(string task, string person), BoolVar> _personUses = new();
    private readonly Dictionary<string, List<IntervalVar>> _personIntervals = new();
    private readonly List<LinearExpr> _priorityObjectives = new();
    
    private readonly Dictionary<string, CalendarLogic>  _calendars = new();
    
    public bool Solve(ProjectDto project, int? maxSeconds = null)
    {
        _project = project;

        // Разворачивает дерево задач проекта в плоский список задач,
        // которые нужно планировать (с учётом приоритетов/весов).
        _taskAlignment = new TaskAlignment();
        _taskAlignment.Alignment(_project, _weights);

        _model = new CpModel();

        // Строит все переменные и ограничения модели (см. PrepareSolverConstraints).
        PrepareSolverConstraints();

        // Цель солвера - минимизировать общий срок проекта (makespan, то есть
        // максимальный end среди всех планируемых задач) плюс сумму
        // приоритетных штрафов за поздний старт (см. _weights).
        var makespan = _model.NewIntVar(0, Horizon, "makespan");
        _model.AddMaxEquality(makespan, _taskAlignment.FlattenTasksToSolve.Select(t => _ends[t.Key]).ToArray());
        _model.Minimize(makespan + LinearExpr.Sum(_priorityObjectives));
        //model.Minimize(makespan);

        var solver = new CpSolver();

        if (maxSeconds is not null)
        {
            solver.StringParameters = $"max_time_in_seconds:{maxSeconds}";
        }

        var status = solver.Solve(_model);
        
        var solved = status is CpSolverStatus.Optimal or CpSolverStatus.Feasible;

        if (solved)
        {
            PostFillAfterSolve(solver);
        }
        
        // var proto = _model.Model;
        // var response = solver.Response!;
        //
        // for (int i = 0; i < proto.Variables.Count; i++)
        // {
        //     var v = proto.Variables[i];
        //     var val = response.Solution[i];
        //     Console.WriteLine($"{v.Name} = {val}");
        // }
        // foreach (var c in proto.Constraints)
        // {
        //     Console.WriteLine(c);
        // }
        
        return solved;
    }

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
            // personUseVar в 1 (см. CreateTasksIntervals).
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
            // CreateTasksIntervals) уже известный, реальный остаток чужой,
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
                var lastProgressRec = task.Fact!.Records.Last(x =>
                    x.Type is TaskFactRecordType.Started or TaskFactRecordType.InProgress
                        or TaskFactRecordType.CorrectedDuration && x.Duration is not null);

                if (lastProgressRec.RecordedAt < _project.FactDate)
                {
                    var calendar = _calendars[resource.Name];

                    // Оставшаяся длительность = длительность на момент последней
                    // отметки минус число рабочих дней ресурса, фактически
                    // прошедших между стартом и датой среза.
                    var remainingDur = lastProgressRec.Duration - calendar.CalcWorkingDaysCount(calcedStart, _project.FactDate.Value);

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
        CreateIntervalsAndPriorityObjectives();
        CreateDependencyBetweenTasks();
        CreateResourceRestrictions();
        CreateTasksIntervals();
    }

    // Для каждой планируемой задачи перебирает всех кандидатов-ресурсов
    // (см. FindResourcesForTask) и строит для каждого из них "опциональный"
    // интервал (задействуется только если солвер выберет именно этот
    // ресурс). Длительность каждого такого интервала растягивается на число
    // нерабочих дней ресурса, которые попадают внутрь интервала, - так
    // рабочая длительность задачи (в бизнес-днях) превращается в календарную.
    // В конце для задачи выбирается ровно один ресурс (AddExactlyOne), а для
    // каждого ресурса все его интервалы (включая "перерывы" из
    // CreateResourceRestrictions) не должны пересекаться (AddNoOverlap).
    private void CreateTasksIntervals()
    {
        foreach (var taskKv in _taskAlignment.FlattenTasksToSolve)
        {
            var taskKey = taskKv.Key;
            var task = taskKv.Value;

            var resources = FindResourcesForTask(task);

            var useList = new HashSet<BoolVar>();

            foreach (var resource in resources)
            {
                var resourceCalendar = _calendars[resource.Name];

                var taskInProgress = task.Fact?.IsProgress == true;
                var lastProgressRec = task.Fact?.Records.LastOrDefault(x =>
                    x.Type is TaskFactRecordType.Started or TaskFactRecordType.InProgress
                        or TaskFactRecordType.CorrectedDuration && x.Duration is not null);

                // Если задача уже в работе, берём фактически оставшуюся
                // длительность из последней записи факта, иначе считаем её
                // по лимитам задачи и параметрам ресурса.
                var duration = 0;
                if (taskInProgress && lastProgressRec is not null)
                {
                    duration = lastProgressRec.Duration!.Value;
                }

                if(duration == 0)
                {
                    duration = CalcTaskLimitDuration(task, resource);
                }

                var nonWorkingDays = resourceCalendar.NonWorkingDays;
                var nonWorkingDaysCount = nonWorkingDays.Count;

                // dur - это длительность интервала в календарных днях. Нижняя
                // граница - рабочая длительность без единого нерабочего дня
                // внутри; верхняя - на случай, если интервал заденет вообще
                // все нерабочие дни ресурса в горизонте планирования.
                // Фактическое значение зафиксируется ниже constraint'ом
                // dur == duration + Σcross.
                var dur = _model.NewIntVar(duration, duration + nonWorkingDaysCount, $"dur_nonwork_{taskKey}_{resource.Name}");

                IntervalVar interval;
                
                BoolVar personUseVar;
            
                // if task already in progress, we will add hard constraints
                if (taskInProgress)
                {
                    var fixedStart = lastProgressRec!.RecordedAt.DayNumber - _project.ProjectStart.DayNumber;
                
                    // started - day has been begun, other statuses - day finished, i.e. next range calc as +1 day
                    if (lastProgressRec.Type != TaskFactRecordType.Started)
                    {
                        fixedStart++;
                    }

                    _starts[taskKey] = _model.NewConstant(fixedStart);
                
                    //interval = _model.NewIntervalVar(_starts[taskKey], dur, _ends[taskKey], $"fix_{taskKey}_{resource.Name}");
                    
                    personUseVar = _model.NewBoolVar($"force_use_{taskKey}_{resource.Name}");
                    _model.Add(personUseVar == 1);
                    
                    interval = _model.NewOptionalIntervalVar(_starts[taskKey], dur, _ends[taskKey], personUseVar, $"fix_{taskKey}_{resource.Name}");                    
                }
                else
                {
                    personUseVar = _model.NewBoolVar($"use_{taskKey}_{resource.Name}");
                
                    interval = _model.NewOptionalIntervalVar(_starts[taskKey], dur, _ends[taskKey], personUseVar,
                        $"opt_{taskKey}_{resource.Name}");
                }
                
                _personIntervals[resource.Name].Add(interval);
                _personUses[(taskKey, resource.Name)] = personUseVar;
                useList.Add(personUseVar);

                // Для каждого нерабочего дня ресурса ("day") нужно определить,
                // попадает ли он внутрь интервала [start, end) этой задачи у
                // этого ресурса. Заводим три булевы переменные на день:
                //   beforeEnd  ⇔ start ≤ day   (задача уже началась к этому дню)
                //   afterStart ⇔ end > day     (задача ещё не закончилась к этому дню)
                //   cross      ⇔ beforeEnd ∧ afterStart (день реально внутри интервала)
                // Каждая переменная жёстко привязывается к start/end двумя
                // constraint'ами (прямым и обратным), независимо от значения
                // cross - только так cross гарантированно совпадает с реальным
                // положением дел, а не может быть выставлен солвером "просто
                // так" (именно рассинхронизация cross с beforeEnd/afterStart
                // раньше приводила к тому, что длительность задачи могла
                // раздуваться на пустом месте).
                // Связь cross с beforeEnd/afterStart задаётся в обе стороны:
                // AddBoolAnd - "cross ⇒ beforeEnd и afterStart",
                // AddBoolOr  - "beforeEnd и afterStart ⇒ cross".
                var currentCrosses = new HashSet<BoolVar>();
                foreach (var day in nonWorkingDays)
                {
                    var cross = _model.NewBoolVar($"cross_vac_{day}_{resource.Name}_{taskKey}");
                    var beforeEnd = _model.NewBoolVar($"before_vac_end_{day}_{resource.Name}_{taskKey}");
                    var afterStart = _model.NewBoolVar($"after_vac_start_{day}_{resource.Name}_{taskKey}");

                    _model.Add(_starts[taskKey] < day + 1).OnlyEnforceIf([beforeEnd, personUseVar]);
                    _model.Add(_starts[taskKey] >= day + 1).OnlyEnforceIf([beforeEnd.Not(), personUseVar]);

                    _model.Add(_ends[taskKey] > day).OnlyEnforceIf([afterStart, personUseVar]);
                    _model.Add(_ends[taskKey] <= day).OnlyEnforceIf([afterStart.Not(), personUseVar]);

                    _model.AddBoolAnd([beforeEnd, afterStart]).OnlyEnforceIf([cross, personUseVar]);
                    _model.AddBoolOr([cross, beforeEnd.Not(), afterStart.Not()]).OnlyEnforceIf(personUseVar);

                    currentCrosses.Add(cross);
                }

                // Календарная длительность = рабочая длительность + число
                // нерабочих дней, реально захваченных интервалом задачи.
                // end пересчитывается из start и итоговой dur.
                _model.Add(dur == duration + LinearExpr.Sum(currentCrosses)).OnlyEnforceIf(personUseVar);
                _model.Add(_ends[taskKey] == _starts[taskKey] + dur).OnlyEnforceIf(personUseVar);
            }
            
            // each task can have only one resource
            if (useList.Any())
            {
                _model.AddExactlyOne(useList);
            }
        }
        
        // no overlaps in tasks for resource
        foreach (var kv in _personIntervals)
        {
            _model.AddNoOverlap(kv.Value);
        }
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

            // Ресурс недоступен после AvailTo - "занимаем" его до конца горизонта.
            if (resource.AvailTo.HasValue)
            {
                var from = resource.AvailTo <= _project.ProjectStart
                    ? 0
                    : resource.AvailTo.Value.DayNumber - _project.ProjectStart.DayNumber;

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
        // Есть ли в проекте вообще хоть одна уже завершённая задача - т.е.
        // мы точно не первый раз решаем этот проект с чистого листа, а
        // перепланируем то, что уже частично прожито. Только в этом случае
        // имеет смысл запрещать свежим задачам стартовать раньше даты среза
        // (см. ниже) - на самом первом решении "с нуля" бэкдейтинг легитимен
        // (например, заводим в систему уже идущий проект и просим солвер
        // восстановить, что по датам должно было случиться раньше FactDate).
        var hasAlreadyCompletedWork = _taskAlignment.OriginalTasks.Values.Any(t => t.Fact?.IsFinished == true);

        foreach (var taskKv in _taskAlignment.FlattenTasksToSolve)
        {
            var task = taskKv.Value;

            foreach (var predecessorId in task.Limit!.PredecessorIds)
            {
                // PredecessorResolver уже вырезал из PredecessorIds всех
                // Disabled/Paused/Finished предшественников (см.
                // TaskAlignment/PredecessorResolver.cs) - у любого id,
                // дошедшего сюда, гарантированно есть start/end в модели.
                Debug.Assert(!_taskAlignment.FlattenTasksCopy[predecessorId].CanSkipTask,
                    $"Predecessor {predecessorId} should have been filtered out by PredecessorResolver");

                _model.Add(_starts[task.Id] >= _ends[predecessorId]);
            }

            if (task.Limit.DueDate is not null)
            {
                var dueDate = task.Limit.DueDate.Value.DayNumber - _project.ProjectStart.DayNumber;
                _model.Add(_ends[task.Id] <= dueDate);
            }

            if (task.Limit.StartAfter is not null)
            {
                var startAfter = task.Limit.StartAfter.Value.DayNumber - _project.ProjectStart.DayNumber;
                _model.Add(_starts[task.Id] >= startAfter);
            }

            // Задача без факта прогресса (ещё по-настоящему не начата) не
            // может стартовать раньше даты среза - иначе задачи, уже ставшие
            // Completed по предыдущей FactDate и переставшие быть интервалом
            // в модели (см. CanSkipTask), оставляют в календарном прошлом
            // "свободное" на вид время, куда солвер может без всякого смысла
            // поставить совершенно новую задачу, которую физически
            // невозможно было начать раньше, чем её вообще добавили в план.
            // Уже начатую задачу (Fact.IsProgress) не трогаем - её старт и
            // так жёстко зафиксирован датой факта ниже, в CreateTasksIntervals,
            // и та дата вполне законно может быть раньше текущей FactDate.
            if (hasAlreadyCompletedWork && _project.FactDate is not null && task.Fact?.IsProgress != true)
            {
                var factDateOffset = _project.FactDate.Value.DayNumber - _project.ProjectStart.DayNumber;
                if (factDateOffset > 0)
                {
                    _model.Add(_starts[task.Id] >= factDateOffset);
                }
            }
        }
    }

    // Создаёт по паре переменных start/end (в днях от ProjectStart, в
    // пределах горизонта) на каждую планируемую задачу - на них дальше
    // ссылаются все остальные ограничения. Заодно формирует слагаемое
    // целевой функции "start * вес приоритета": чем важнее задача, тем
    // дороже обходится солверу сдвигать её начало вправо.
    private void CreateIntervalsAndPriorityObjectives()
    {
        _starts.Clear();
        _ends.Clear();
        _priorityObjectives.Clear();

        foreach (var taskKv in _taskAlignment.FlattenTasksToSolve)
        {
            var task = taskKv.Value;

            var startIntVar = _model.NewIntVar(0, Horizon, $"start_{task.Id}");
            var endIntVar = _model.NewIntVar(0, Horizon, $"end_{task.Id}");
            _starts[task.Id] = startIntVar;
            _ends[task.Id] = endIntVar;

            var po = startIntVar * _weights[task.Limit!.Priority ?? 0];
            _priorityObjectives.Add(po);
        }
    }

    // Рабочая (бизнес-дневная) длительность задачи для конкретного ресурса:
    // явно заданная Duration или оценка по TShirt-размеру с поправкой на
    // уверенность оценки ресурса (Confidence: 100% - нижняя граница, 0% -
    // верхняя), затем прибавляется буфер, затем длительность увеличивается
    // пропорционально неполной загрузке ресурса на задаче (Percent).
    private int CalcTaskLimitDuration(TaskDto task, ResourceDto resource)
    {
        var duration = task.Limit!.Duration ?? task.Limit.TShirt!.Value.ToDays(resource.Confidence);

        if (task.Limit.Buffer is not null)
        {
            duration += task.Limit.Buffer.Value;
        }

        if (resource.Percent != 100)
        {
            duration = Math.Max(1, Convert.ToInt32(Math.Floor(duration * 100.0 / resource.Percent)));
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
                throw new Exception($"Can't find {resource}, for task {task.Id}");
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