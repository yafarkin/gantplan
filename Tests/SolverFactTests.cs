using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;

namespace Tests;

public sealed class SolverFactTests
{
    private ProjectDto _project = null!;
    private Solver _solver = null!;
    
    [SetUp]
    public void Setup()
    {
        _solver = new Solver();
    }

    private static ProjectDto TwoTaskProject(DateOnly? factDate) => new()
    {
        ProjectStart = new DateOnly(2026, 1, 5),
        FactDate = factDate,
        RootTask = new TaskDto
        {
            Id = "1",
            Name = "root",
            Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
            Children = [
                new TaskDto
                {
                    Id = "2",
                    Name = "short - by plan done long before FactDate",
                    Limit = new TaskLimitDto { Duration = 5, ResourceRole = "dev" }
                },
                new TaskDto
                {
                    Id = "3",
                    Name = "long - after short",
                    Limit = new TaskLimitDto { Duration = 20, ResourceRole = "dev", PredecessorIds = ["2"] }
                }
            ]
        },
        Resources = [new ResourceDto { Role = "dev", Name = "john" }]
    };

    private TaskDto Task(string id) => _project.RootTask.Children.Single(t => t.Id == id);

    private static TaskFactRecordDto Completed(int month, int day) => new()
    {
        RecordedAt = new DateOnly(2026, month, day),
        Type = TaskFactRecordType.Completed,
        ResourceName = "john"
    };

    // Ни одной ручной записи факта: по FactDate солвер сам отмечает, что
    // задача начата по плану, и пишет остаток на дату среза (IsAuto). Но
    // закрытие сам не ставит никогда: "2" по плану кончилась 09.01, а
    // подтверждения нет - значит, она ещё не сделана, тянется до ближайшего
    // рабочего дня после FactDate (21.01) и держит за собой "3".
    [Test]
    public void FactDateShouldAutoStartButRequireClosureConfirmationTest()
    {
        _project = TwoTaskProject(new DateOnly(2026, 1, 20));

        Assert.IsTrue(_solver.Solve(_project));

        var task2 = Task("2");
        Assert.That(task2.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 5)));
        Assert.That(task2.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 21)));
        Assert.That(task2.Plan!.AwaitingClosure, Is.True);
        Assert.That(_solver.AwaitingClosure, Is.EqualTo(new[] { task2 }));
        Assert.That(task2.Fact!.IsFinished, Is.False);
        Assert.That(task2.Fact.Records.All(r => r.IsAuto), Is.True);
        Assert.That(task2.Fact.Records.Select(r => (r.Type, r.RecordedAt, r.Duration)), Is.EqualTo(new[]
        {
            (TaskFactRecordType.Started, new DateOnly(2026, 1, 5), (int?)5),
            (TaskFactRecordType.InProgress, new DateOnly(2026, 1, 20), (int?)1)
        }));

        // "3" ждёт закрытия "2" и стартует после 21.01: 20 рабочих дней с
        // 22.01 - до 18.02. К FactDate она не начата - факта у неё нет.
        var task3 = Task("3");
        Assert.That(task3.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 22)));
        Assert.That(task3.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 2, 18)));
        Assert.That(task3.Plan!.AwaitingClosure, Is.False);
        Assert.That(task3.Fact, Is.Null);
    }

    // Воспроизводит на минимальном примере сценарий DEMO-050/075/100 из
    // демо-проекта: уже завершённая задача, уже начатая (без приоритета) и
    // новая с наивысшим приоритетом, закреплённая за тем же самым уже
    // занятым исполнителем.
    [Test]
    public void PriorityTaskShouldWaitForResourceBusyOnInProgressTaskTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    // аналог DEMO-050 - целиком в прошлом, Fact.IsFinished ==
                    // true, поэтому у солвера для неё вообще нет переменной
                    // start/end (CanSkipTask) - Plan/Fact заданы руками и
                    // ничем, кроме бага, измениться не могут.
                    new TaskDto
                    {
                        Id = "finished",
                        Name = "already finished before the project even started",
                        Limit = new TaskLimitDto { Duration = 2, ResourceRole = "dev" },
                        Plan = new TaskPlanDto
                        {
                            ResourceName = "john",
                            PlannedStart = new DateOnly(2025, 12, 29),
                            PlannedFinish = new DateOnly(2025, 12, 30)
                        },
                        Fact = new TaskFactDto
                        {
                            Records =
                            [
                                new TaskFactRecordDto
                                {
                                    RecordedAt = new DateOnly(2025, 12, 30),
                                    Type = TaskFactRecordType.Completed,
                                    ResourceName = "john"
                                }
                            ]
                        }
                    },
                    // аналог DEMO-075 - уже в работе (Fact.IsProgress == true),
                    // без приоритета; в Fact - конкретный исполнитель, а не
                    // просто роль.
                    new TaskDto
                    {
                        Id = "inProgress",
                        Name = "already in progress, no priority",
                        Limit = new TaskLimitDto { Duration = 30, ResourceRole = "dev" },
                        Fact = new TaskFactDto
                        {
                            Records =
                            [
                                new TaskFactRecordDto
                                {
                                    RecordedAt = new DateOnly(2026, 1, 5),
                                    Type = TaskFactRecordType.InProgress,
                                    ResourceName = "john",
                                    Duration = 15
                                }
                            ]
                        }
                    },
                    // аналог DEMO-100 - наивысший приоритет, но закреплена
                    // (ResourceName, не просто ResourceRole) за тем же
                    // john'ом, который прямо сейчас занят на "inProgress".
                    new TaskDto
                    {
                        Id = "priority",
                        Name = "priority 1, pinned to the same busy resource",
                        Limit = new TaskLimitDto { Priority = 1, Duration = 10, ResourceName = "john" }
                    }
                ]
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        var solved = _solver.Solve(_project);
        Assert.IsTrue(solved);

        var finished = _project.RootTask.Children.First(t => t.Id == "finished");
        var inProgress = _project.RootTask.Children.First(t => t.Id == "inProgress");
        var priority = _project.RootTask.Children.First(t => t.Id == "priority");

        // "finished" не изменилась ни на день - как и было задано руками.
        Assert.That(finished.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2025, 12, 29)));
        Assert.That(finished.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2025, 12, 30)));

        // "inProgress" продолжается с зафиксированной даты (RecordedAt + 1,
        // т.к. запись типа InProgress - "уже прошедший" день) - несмотря на
        // то, что рядом появилась приоритетная задача на того же человека.
        Assert.That(inProgress.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 6)));
        Assert.That(inProgress.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 26)));
        Assert.That(inProgress.Plan!.ResourceName, Is.EqualTo("john"));

        // "priority" (приоритет 1!) встаёт только на следующий рабочий день
        // после того, как john освобождается с "inProgress" - приоритет не
        // даёт обойти уже занятого исполнителя.
        Assert.That(priority.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 27)));
        Assert.That(priority.Plan!.ResourceName, Is.EqualTo("john"));
        Assert.That(priority.Plan!.PlannedStart, Is.GreaterThan(inProgress.Plan!.PlannedFinish));
    }

    // Limit задаёт только роль ("кто-то из dev"), т.е. формально jane - точно
    // такой же кандидат, как john, и вдобавок jane полностью свободна с
    // самого начала - для голого "минимизировать makespan" john (с его
    // отложенным из-за Fact стартом) был бы объективно ХУЖЕ выбором, чем
    // jane. Если солвер всё равно ставит john - значит "уже начатую задачу
    // нельзя переназначить" (см. Solver.FindResourcesForTask) реально
    // работает, а не просто совпадает с тем, что и так выбрал бы оптимизатор.
    [Test]
    public void InProgressTaskShouldNotBeReassignedToFreeAlternativeResourceTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "inProgress",
                        Name = "in progress, role-only limit - jane is a free alternative",
                        Limit = new TaskLimitDto { Duration = 10, ResourceRole = "dev" },
                        Fact = new TaskFactDto
                        {
                            Records =
                            [
                                new TaskFactRecordDto
                                {
                                    RecordedAt = new DateOnly(2026, 1, 8),
                                    Type = TaskFactRecordType.InProgress,
                                    ResourceName = "john",
                                    Duration = 6
                                }
                            ]
                        }
                    }
                ]
            },
            Resources =
            [
                new ResourceDto { Role = "dev", Name = "john" },
                new ResourceDto { Role = "dev", Name = "jane" }
            ]
        };

        var solved = _solver.Solve(_project);
        Assert.IsTrue(solved);

        var task = _project.RootTask.Children.First();

        // если бы солвер вместо john взял свободную jane, задача стартовала
        // бы 05.01 (ProjectStart), а не 09.01 (день после Fact-записи) -
        // разница в датах и есть доказательство, что выбор не "случайно
        // совпал", а вынужден фиксацией исполнителя.
        Assert.That(task.Plan!.ResourceName, Is.EqualTo("john"));
        Assert.That(task.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 9)));
        Assert.That(task.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 16)));
    }

    // Подтверждённое закрытие: "2" закрыта 09.01 - её в модели больше нет,
    // даты для таймлайна восстановлены (05.01-09.01), а "3" идёт сразу
    // следом, с 12.01, и к FactDate (20.01) в работе: остаток 20 - 7
    // прошедших рабочих дней = 13.
    [Test]
    public void ConfirmedClosureShouldLetSuccessorStartRightAfterItTest()
    {
        _project = TwoTaskProject(new DateOnly(2026, 1, 20));
        Task("2").Fact = new TaskFactDto { Records = [Completed(1, 9)] };

        Assert.IsTrue(_solver.Solve(_project));

        var task2 = Task("2");
        Assert.That(task2.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 5)));
        Assert.That(task2.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 9)));
        Assert.That(task2.Plan!.ResourceName, Is.EqualTo("john"));
        Assert.That(task2.Fact!.Records, Has.Count.EqualTo(1));

        var task3 = Task("3");
        Assert.That(task3.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 12)));
        Assert.That(task3.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 2, 6)));
        Assert.That(task3.Plan!.AwaitingClosure, Is.False);
        Assert.That(task3.Fact!.Records.Select(r => (r.Type, r.Duration, r.IsAuto)), Is.EqualTo(new[]
        {
            (TaskFactRecordType.Started, (int?)20, true),
            (TaskFactRecordType.InProgress, (int?)13, true)
        }));
    }

    // Повторный Solve на том же ProjectDto (как при перечитывании
    // project.json): авто-записи прошлого прогона не копятся и не
    // "замораживают" план - перед решением они удаляются и считаются
    // заново по новой FactDate. Ручное закрытие, добавленное между
    // прогонами, учитывается сразу.
    [Test]
    public void SecondSolveShouldRecomputeAutoFactsInsteadOfAccumulatingThemTest()
    {
        _project = TwoTaskProject(new DateOnly(2026, 1, 20));
        Assert.IsTrue(_solver.Solve(_project));
        Assert.That(Task("2").Fact!.Records, Has.Count.EqualTo(2));

        // дата среза уходит вперёд (01.03 - воскресенье, ближайший рабочий
        // день - 02.03); "2" по-прежнему не закрыта
        _project.FactDate = new DateOnly(2026, 3, 1);
        Assert.IsTrue(_solver.Solve(_project));

        var task2 = Task("2");
        Assert.That(task2.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 3, 2)));
        Assert.That(task2.Fact!.Records.Select(r => (r.Type, r.RecordedAt)), Is.EqualTo(new[]
        {
            (TaskFactRecordType.Started, new DateOnly(2026, 1, 5)),
            (TaskFactRecordType.InProgress, new DateOnly(2026, 3, 1))
        }));
        Assert.That(Task("3").Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 3, 3)));

        // подтвердили закрытие "2" - "3" пересчитывается от него: по плану
        // 12.01-06.02, но тоже не закрыта, поэтому тянется до 02.03
        task2.Fact.Records.Add(Completed(1, 9));
        Assert.IsTrue(_solver.Solve(_project));

        Assert.That(task2.Fact.Records.Select(r => r.Type), Is.EqualTo(new[] { TaskFactRecordType.Completed }));
        var task3 = Task("3");
        Assert.That(task3.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 12)));
        Assert.That(task3.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 3, 2)));
        Assert.That(task3.Plan!.AwaitingClosure, Is.True);
    }

    // Закрытые задачи в модели не участвуют, и без границы истории их время
    // в прошлом выглядело бы свободным. "old" закрыта john'ом 15.01 - новую
    // задачу ему раньше 16.01 не ставят (иначе она встала бы на 05.01).
    // Дальше она по плану к FactDate уже сделана, но не закрыта - ждёт
    // подтверждения.
    [Test]
    public void TaskShouldNotStartBeforeResourcesLastConfirmedClosureTest()
    {
        _project = HistoryProject(startAfter: null);

        Assert.IsTrue(_solver.Solve(_project));

        // даты закрытой задачи восстановлены для таймлайна: 3 рабочих дня,
        // последний - день закрытия
        var old = Task("old");
        Assert.That(old.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 13)));
        Assert.That(old.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 15)));

        var fresh = Task("new");
        Assert.That(fresh.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 16)));
        Assert.That(fresh.Plan!.AwaitingClosure, Is.True);
    }

    // Совсем новую задачу, добавленную в план только сейчас, от постановки
    // "задним числом" защищает StartAfter.
    [Test]
    public void StartAfterShouldKeepFreshTaskOutOfThePastTest()
    {
        _project = HistoryProject(startAfter: new DateOnly(2026, 3, 2));

        Assert.IsTrue(_solver.Solve(_project));

        var fresh = Task("new");
        Assert.That(fresh.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 3, 2)));
        Assert.That(fresh.Plan!.AwaitingClosure, Is.False);
        Assert.That(fresh.Fact, Is.Null);
    }

    private static ProjectDto HistoryProject(DateOnly? startAfter) => new()
    {
        ProjectStart = new DateOnly(2026, 1, 5),
        FactDate = new DateOnly(2026, 3, 1),
        RootTask = new TaskDto
        {
            Id = "1",
            Name = "root",
            Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
            Children = [
                new TaskDto
                {
                    Id = "old",
                    Name = "closed history, dates not given",
                    Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev" },
                    Fact = new TaskFactDto { Records = [Completed(1, 15)] }
                },
                new TaskDto
                {
                    Id = "new",
                    Name = "no facts at all",
                    Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev", StartAfter = startAfter }
                }
            ]
        },
        Resources = [new ResourceDto { Role = "dev", Name = "john" }]
    };

    // По плану/факту 15.01 оставалось 8 дней (обычный автоматический
    // пересчёт), но 22.01 кто-то вручную скорректировал остаток на 15 дней
    // (например, вскрылась недооценённая сложность) - CorrectedDuration
    // должна победить как "последняя запись прогресса" (см.
    // Solver.FindLastProgressRecord: ищет среди Started,
    // InProgress И CorrectedDuration), а не более ранняя InProgress-оценка.
    [Test]
    public void CorrectedDurationShouldOverrideEarlierProgressEstimateTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "task",
                        Name = "in progress, then manually corrected",
                        Limit = new TaskLimitDto { Duration = 20, ResourceRole = "dev" },
                        Fact = new TaskFactDto
                        {
                            Records =
                            [
                                new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 5), Type = TaskFactRecordType.Started, ResourceName = "john", Duration = 20 },
                                new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 15), Type = TaskFactRecordType.InProgress, ResourceName = "john", Duration = 8 },
                                new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 22), Type = TaskFactRecordType.CorrectedDuration, ResourceName = "john", Duration = 15 }
                            ]
                        }
                    }
                ]
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));

        var task = _project.RootTask.Children.First();

        // старт = день после CorrectedDuration (22.01 + 1), а не после
        // InProgress (15.01 + 1) - и остаток - 15 дней, а не 8.
        Assert.That(task.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 23)));
        Assert.That(task.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 2, 12)));
        Assert.That(task.Plan!.ResourceName, Is.EqualTo("john"));
    }

    // Регрессия на реальный баг, найденный на живом двойном прогоне Planner
    // (DemoProject -> project.json -> читаем project.json заново): задаче на
    // входе задали только InProgress (типичная запись "уже наполовину
    // сделана"), без отдельно заведённой Started. PostFillAfterSolve считал
    // это "задачу ещё вообще не трогали" и молча дописывал вторую,
    // конкурирующую Started-запись с длительностью, пересчитанной заново из
    // Limit.Duration (тут - 60, заведомо больше настоящих 10 оставшихся) -
    // именно она потом побеждала как "последняя запись прогресса" и на
    // следующем прогоне фиксировала совершенно неверный (сильно
    // раздутый) остаток, из-за чего у одного исполнителя схлопывались две
    // не пересекавшиеся раньше задачи и Solve() начинал возвращать false.
    [Test]
    public void ExistingInProgressRecordShouldNotBeOverriddenBySyntheticStartedTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            FactDate = new DateOnly(2026, 1, 10),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "task",
                        Name = "already in progress, no preceding Started record",
                        Limit = new TaskLimitDto { Duration = 60, ResourceRole = "dev" },
                        Fact = new TaskFactDto
                        {
                            Records =
                            [
                                new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 5), Type = TaskFactRecordType.InProgress, ResourceName = "john", Duration = 10 }
                            ]
                        }
                    }
                ]
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));

        var task = _project.RootTask.Children.First();

        // никакой синтетической Started-записи не появилось - была и
        // осталась ровно одна исходная InProgress-запись, плюс её
        // корректно пересчитанное продолжение по FactDate.
        Assert.That(task.Fact!.Records.Select(r => r.Type), Is.EqualTo(new[]
        {
            TaskFactRecordType.InProgress,
            TaskFactRecordType.InProgress
        }));

        // план отталкивается от исходных 10 дней (плюс пересчёт по
        // FactDate), а не от 60-дневной оценки Limit.Duration.
        Assert.That(task.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 6)));
        Assert.That(task.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 19)));
        Assert.That(task.Fact.Records[1].Duration, Is.EqualTo(6));
    }

    // Started без Duration - задача в работе, но остаток не указан. Раньше
    // солвер падал NullReferenceException'ом (искал последнюю запись
    // прогресса только среди записей с Duration); теперь старт берётся из
    // этой записи, а длительность - из лимитов задачи.
    [Test]
    public void StartedRecordWithoutDurationShouldUseLimitDurationTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Limit = new TaskLimitDto { Duration = 5, ResourceRole = "dev" },
                Fact = new TaskFactDto
                {
                    Records =
                    [
                        new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 6), Type = TaskFactRecordType.Started, ResourceName = "john" }
                    ]
                }
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));
        // 06.01 (вт) - 09.01 (пт) - 4 дня, 12.01 (пн) - пятый.
        Assert.That(_project.RootTask.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 6)));
        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 12)));
    }

    // Пауза снимается следующей записью прогресса: Paused 07.01, затем
    // InProgress 12.01 (осталось 8) - задача снова в работе, с 13.01.
    // Раньше хватало любой записи Paused в истории - и задача навсегда
    // выпадала из плана.
    [Test]
    public void ProgressRecordAfterPauseShouldResumeTaskTest()
    {
        _project = SingleTaskProject(
            new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 5), Type = TaskFactRecordType.Started, ResourceName = "john", Duration = 10 },
            new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 7), Type = TaskFactRecordType.Paused, ResourceName = "john" },
            new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 12), Type = TaskFactRecordType.InProgress, ResourceName = "john", Duration = 8 });

        Assert.IsTrue(_solver.Solve(_project));

        // 13.01 (вт) + 8 рабочих дней - до 22.01
        Assert.That(_project.RootTask.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 13)));
        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 22)));
    }

    // Старт по факту приходится на выходной (InProgress записан в пятницу -
    // остаток со следующего дня, субботы) - переносится на понедельник.
    [Test]
    public void FixedStartOnWeekendShouldMoveToNextWorkingDayTest()
    {
        _project = SingleTaskProject(
            new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 9), Type = TaskFactRecordType.InProgress, ResourceName = "john", Duration = 2 });

        Assert.IsTrue(_solver.Solve(_project));

        Assert.That(_project.RootTask.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 12)));
        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 13)));
    }

    // Остаток 0 - работа сделана, но закрытие не подтверждено: задача ждёт
    // подтверждения и держит исполнителя ближайший рабочий день после
    // FactDate (FactDate - пятница 09.01, значит понедельник 12.01).
    [Test]
    public void ZeroRemainingWithoutClosureShouldAwaitConfirmationTest()
    {
        _project = SingleTaskProject(
            new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 9), Type = TaskFactRecordType.InProgress, ResourceName = "john", Duration = 0 });
        _project.FactDate = new DateOnly(2026, 1, 9);

        Assert.IsTrue(_solver.Solve(_project));

        Assert.That(_project.RootTask.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 12)));
        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 12)));
        Assert.That(_project.RootTask.Plan!.AwaitingClosure, Is.True);
    }

    // DueDate, который уже не выполнить (задача в работе, остатка 10 дней
    // не хватает до срока 09.01), не делает весь план невыполнимым:
    // ограничение пропускается, в Warnings - понятное предупреждение.
    [Test]
    public void UnreachableDueDateShouldBeReportedNotFailTheSolveTest()
    {
        _project = SingleTaskProject(
            new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 5), Type = TaskFactRecordType.Started, ResourceName = "john", Duration = 10 });
        _project.RootTask.Limit!.DueDate = new DateOnly(2026, 1, 9);

        Assert.IsTrue(_solver.Solve(_project));

        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 16)));
        Assert.That(_solver.Warnings, Has.Count.EqualTo(1));
        Assert.That(_solver.Warnings[0], Does.Contain("DueDate 09.01.2026"));
    }

    private static ProjectDto SingleTaskProject(params TaskFactRecordDto[] records) => new()
    {
        ProjectStart = new DateOnly(2026, 1, 5),
        RootTask = new TaskDto
        {
            Id = "1",
            Name = "root",
            Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
            Limit = new TaskLimitDto { Duration = 10, ResourceRole = "dev" },
            Fact = new TaskFactDto { Records = records.ToList() }
        },
        Resources = [new ResourceDto { Role = "dev", Name = "john" }]
    };
}
