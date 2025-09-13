using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using Google.OrTools.Sat;
using LinearExpr = Google.OrTools.Sat.LinearExpr;

namespace GantPlan.Logic;

public sealed class Solver
{
    private const int Horizon = 180;

    private ProjectDto _project = null!;
    
    private TaskAlignment _taskAlignment = null!;
    
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
        
        _taskAlignment = new TaskAlignment();
        _taskAlignment.Alignment(_project, _weights);
        
        _model = new CpModel();

        PrepareSolverConstraints();
        
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

        return solved;
    }

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
            var calcedEnd = projectStart.AddDays(ed - 1);

            task.Fact ??= new TaskFactDto();
            task.Plan ??= new TaskPlanDto
            {
                PlannedStart = calcedStart
            };

            task.Plan.PlannedFinish = calcedEnd;

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

            if (task.Fact.Records.All(x => x.Type != TaskFactRecordType.Started))
            {
                task.Fact.Records.Add(new TaskFactRecordDto
                {
                    RecordedAt = calcedStart,
                    ResourceName = resource.Name,
                    Duration = CalcTaskLimitDuration(task, resource),
                    Type = TaskFactRecordType.Started
                });
            }

            if (_project.FactDate is null)
            {
                continue;
            }

            if (_project.FactDate >= calcedEnd)
            {
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
                var lastProgressRec = task.Fact!.Records.Last(x =>
                    x.Type is TaskFactRecordType.Started or TaskFactRecordType.InProgress
                        or TaskFactRecordType.CorrectedDuration && x.Duration is not null);

                if (lastProgressRec.RecordedAt < _project.FactDate)
                {
                    var calendar = _calendars[resource.Name];
                    
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

    private void PrepareSolverConstraints()
    {
        CreateIntervalsAndPriorityObjectives();
        CreateDependencyBetweenTasks();
        CreateResourceRestrictions();
        CreateTasksIntervals();
    }

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
                
                var dur = _model.NewIntVar(duration, duration + nonWorkingDaysCount, $"dur_nonwork_{taskKey}_{resource.Name}");

                IntervalVar interval;
            
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
                
                    interval = _model.NewIntervalVar(_starts[taskKey], dur, _ends[taskKey], $"fix_{taskKey}_{resource.Name}");
                }
                else
                {
                    var personUseVar = _model.NewBoolVar($"use_{taskKey}_{resource.Name}");
                    _personUses[(taskKey, resource.Name)] = personUseVar;
                    useList.Add(personUseVar);
                
                    interval = _model.NewOptionalIntervalVar(_starts[taskKey], dur, _ends[taskKey], personUseVar,
                        $"opt_{taskKey}_{resource.Name}");
                }
                
                var currentCrosses = new HashSet<BoolVar>();
                foreach (var t in nonWorkingDays)
                {
                    var vacS = t;
                    var vacE = t + 1;

                    var cross = _model.NewBoolVar($"cross_vac_{vacS}_{resource.Name}_{taskKey}");
                    var beforeEnd = _model.NewBoolVar($"before_vac_end_{vacS}_{resource.Name}_{taskKey}");
                    var afterStart = _model.NewBoolVar($"after_vac_start_{vacS}_{resource.Name}_{taskKey}");

                    _model.Add(_starts[taskKey] < vacE).OnlyEnforceIf([cross, beforeEnd]);
                    _model.Add(_starts[taskKey] >= vacE).OnlyEnforceIf([cross.Not(), beforeEnd.Not()]);

                    _model.Add(_ends[taskKey] > vacS).OnlyEnforceIf([cross, afterStart]);
                    _model.Add(_ends[taskKey] <= vacS).OnlyEnforceIf([cross.Not(), afterStart.Not()]);

                    _model.AddBoolOr([cross, beforeEnd.Not(), afterStart.Not()]);

                    currentCrosses.Add(cross);
                }

                _model.Add(dur == duration + LinearExpr.Sum(currentCrosses));
                _model.Add(_ends[taskKey] == _starts[taskKey] + dur);

                _personIntervals[resource.Name].Add(interval);
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

    private void CreateResourceRestrictions()
    {
        _calendars.Clear();
        
        foreach (var resource in _project.Resources)
        {
            _personIntervals[resource.Name] = [];
            
            if (resource.AvailFrom > _project.ProjectStart)
            {
                var dateShift = resource.AvailFrom.Value.DayNumber - _project.ProjectStart.DayNumber;
                var breakInterval = _model.NewIntervalVar(0, dateShift, dateShift, $"brk_from_{resource.Name}");
                _personIntervals[resource.Name].Add(breakInterval);
            }

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

    private void CreateDependencyBetweenTasks()
    {
        foreach (var taskKv in _taskAlignment.FlattenTasksToSolve)
        {
            var task = taskKv.Value;

            foreach (var predecessorId in task.Limit!.PredecessorIds)
            {
                var predecessor = _taskAlignment.FlattenTasksCopy[predecessorId];
                if (predecessor is { Disabled: false, Fact.IsFinished: true })
                {
                    // task was completed, so we can ignore this constraint
                    continue;
                }

                _model.Add(_starts[task.Id] >= _ends[predecessorId]);
            }
        }
    }

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

    private int CalcTaskLimitDuration(TaskDto task, ResourceDto resource)
    {
        var duration = task.Limit!.Duration ?? task.Limit.TShirt!.Value.ToDays();

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