using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;

namespace Tests;

public sealed class SolverTests
{
    private ProjectDto _project;
    private Solver _solver;
    
    [SetUp]
    public void Setup()
    {
        _solver = new Solver();
    }

    [Test]
    public void SimpleOnTaskAndAssigneeTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "simple task",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Limit = new TaskLimitDto
                {
                    Duration = 5,
                    ResourceRole = "dev"
                }
            },
            Resources = new List<ResourceDto>
            {
                new()
                {
                    Role = "dev",
                    Name = "john"
                }
            }
        };
        
        var solved =  _solver.Solve(_project);
        Assert.IsTrue(solved);

        var expectedResource = _project.Resources.First().Name;
        var expectedStart = new DateOnly(2026, 1, 1);
        var expectedFinish = new DateOnly(2026, 1, 7);

        var plan = _project.RootTask.Plan;
        Assert.IsNotNull(plan);
        Assert.That(expectedResource, Is.EqualTo(plan.ResourceName));
        Assert.That(expectedStart, Is.EqualTo(plan.PlannedStart));
        Assert.That(expectedFinish, Is.EqualTo(plan.PlannedFinish));
    }

    [Test]
    public void FewTasksAndResourcesTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "complex task",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "simple dev task1",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.M,
                            ResourceName = "john",
                        }
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "simple dev task2",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.S,
                            ResourceName = "john",
                            PredecessorIds = ["2"]
                        }
                    },
                    new TaskDto
                    {
                        Id = "4",
                        Name = "simple dev task3",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.S,
                            ResourceRole = "dev"
                        }
                    },
                    new  TaskDto
                    {
                        Id = "5",
                        Name = "simple dev task4",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.XS,
                            ResourceRole = "dev",
                            PredecessorIds = ["4"]
                        }
                    }
                ]
            },
            Resources = new List<ResourceDto>
            {
                new()
                {
                    Role = "dev",
                    Name = "john",
                    AvailFrom = new  DateOnly(2026, 1, 15),
                },
                new()
                {
                    Role = "dev",
                    Name = "doe",
                    Calendar = new CalendarDto
                    {
                        WorkingDays = new List<CalendarPeriod>
                        {
                            new()
                            {
                                From = new DateOnly(2026, 01, 03),
                                To = new DateOnly(2026, 01, 04)
                            }
                        },
                        NonWorkingDays = new List<CalendarPeriod>
                        {
                            new()
                            {
                                From = new DateOnly(2026, 01, 05),
                                To =  new DateOnly(2026, 01, 05)
                            }
                        }
                    }
                }
            }
        };
        
        var solved =  _solver.Solve(_project);
        Assert.IsTrue(solved);

        var expectedResource1 = _project.Resources.First().Name;
        var expectedResource1Confidence = _project.Resources.First().Confidence;
        var expectedResource2 = _project.Resources.Last().Name;

        var tasks = _project.RootTask.Children.ToList();
        
        Assert.That(tasks[0].Plan!.ResourceName, Is.EqualTo(expectedResource1));
        Assert.That(tasks[0].Plan!.PlannedStart, Is.EqualTo(new  DateOnly(2026, 01, 15)));
        Assert.That(tasks[0].Plan!.PlannedFinish, Is.EqualTo(new  DateOnly(2026, 01, 23)));
        Assert.IsNotNull(tasks[0].Fact);
        Assert.That(tasks[0].Fact.IsProgress, Is.True);
        Assert.That(tasks[0].Fact.IsFinished, Is.False);
        Assert.That(tasks[0].Fact.StartDate, Is.EqualTo(tasks[0].Plan!.PlannedStart));
        Assert.That(tasks[0].Fact.FinishDate, Is.Null);
        Assert.That(tasks[0].Fact.Records.Count, Is.EqualTo(1));
        Assert.That(tasks[0].Fact.Records[0].RecordedAt, Is.EqualTo(tasks[0].Plan!.PlannedStart));
        Assert.That(tasks[0].Fact.Records[0].Duration, Is.EqualTo(tasks[0].Limit!.TShirt!.Value.ToDays(expectedResource1Confidence)));
        Assert.That(tasks[0].Fact.Records[0].ResourceName, Is.EqualTo(expectedResource1));
        Assert.That(tasks[0].Fact.Records[0].Type, Is.EqualTo(TaskFactRecordType.Started));
        
        Assert.That(tasks[1].Plan!.ResourceName, Is.EqualTo(expectedResource1));
        Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 24)));
        Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 28)));
        Assert.IsNotNull(tasks[1].Fact);
        Assert.That(tasks[1].Fact.IsProgress, Is.True);
        Assert.That(tasks[1].Fact.IsFinished, Is.False);
        Assert.That(tasks[1].Fact.StartDate, Is.EqualTo(tasks[1].Plan!.PlannedStart));
        Assert.That(tasks[1].Fact.FinishDate, Is.Null);
        Assert.That(tasks[1].Fact.Records.Count, Is.EqualTo(1));
        Assert.That(tasks[1].Fact.Records[0].RecordedAt, Is.EqualTo(tasks[1].Plan!.PlannedStart));
        Assert.That(tasks[1].Fact.Records[0].Duration, Is.EqualTo(tasks[1].Limit!.TShirt!.Value.ToDays(expectedResource1Confidence)));
        Assert.That(tasks[1].Fact.Records[0].ResourceName, Is.EqualTo(expectedResource1));
        Assert.That(tasks[1].Fact.Records[0].Type, Is.EqualTo(TaskFactRecordType.Started));
        
        Assert.That(tasks[2].Plan!.ResourceName, Is.EqualTo(expectedResource2));
        Assert.That(tasks[2].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 1)));
        Assert.That(tasks[2].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 3)));
    }

    [Test]
    public void CalendarTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "complex task",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "simple task 1",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.S,
                            ResourceName = "john",
                        }
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "complex task 2",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.S,
                            ResourceName = "doe",
                        }
                    }
                ]
            },
            Resources = new List<ResourceDto>
            {
                new()
                {
                    Role = "dev",
                    Name = "john",
                    AvailFrom = new DateOnly(2026, 1, 2),
                    Calendar = new CalendarDto
                    {
                        WorkingDays = new List<CalendarPeriod>
                        {
                            new()
                            {
                                From = new DateOnly(2026, 01, 3),
                                To = new DateOnly(2026, 01, 4)
                            }
                        }
                    }
                },
                new()
                {
                    Role = "dev",
                    Name = "doe",
                    AvailFrom = new DateOnly(2026, 1, 3),
                    Calendar = new CalendarDto
                    {
                        NonWorkingDays = new List<CalendarPeriod>
                        {
                            new()
                            {
                                From = new DateOnly(2026, 01, 6),
                                To = new DateOnly(2026, 01, 6)
                            }
                        }
                    }
                }
            }
        };
        
        var solved = _solver.Solve(_project);
        Assert.IsTrue(solved);
        
        var resourceJohn = _project.Resources.Single(j => j.Name == "john");
        var resourceDoe = _project.Resources.Single(j => j.Name == "doe");
        
        var tasks = _project.RootTask.Children.ToList();
        Assert.That(tasks.Count, Is.EqualTo(2));
        
        Assert.That(tasks[0].Plan!.ResourceName, Is.EqualTo(resourceJohn.Name));
        Assert.That(tasks[0].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 2)));
        Assert.That(tasks[0].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 4)));
        
        Assert.That(tasks[1].Plan!.ResourceName, Is.EqualTo(resourceDoe.Name));
        Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 3)));
        Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 8)));
    }

    [Test]
    public void DueDateTest()
    {
         _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "complex task",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "simple task 1",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.L,
                            ResourceRole = "dev"
                        }
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "complex task 2",
                        Limit = new TaskLimitDto
                        {
                            DueDate = new DateOnly(2026, 1, 6),
                            TShirt = TShirtType.S,
                            ResourceRole = "dev"
                        }
                    }
                ]
            },
            Resources = new List<ResourceDto>
            {
                new()
                {
                    Role = "dev",
                    Name = "john"
                },
            }
        };
        
        var solved = _solver.Solve(_project);
        Assert.IsTrue(solved);
       
        var tasks = _project.RootTask.Children.ToList();
        Assert.That(tasks.Count, Is.EqualTo(2));
        
        Assert.That(tasks[0].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 6)));
        Assert.That(tasks[0].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 26)));
        
        Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 1)));
        Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 5)));
    }
    
    [Test]
    public void StartAfterTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "complex task",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "simple task 1",
                        Limit = new TaskLimitDto
                        {
                            StartAfter = new DateOnly(2026, 1, 10),
                            TShirt = TShirtType.L,
                            ResourceRole = "dev"
                        }
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "complex task 2",
                        Limit = new TaskLimitDto
                        {
                            TShirt = TShirtType.S,
                            ResourceRole = "dev"
                        }
                    }
                ]
            },
            Resources = new List<ResourceDto>
            {
                new()
                {
                    Role = "dev",
                    Name = "john"
                },
            }
        };
        
        var solved = _solver.Solve(_project);
        Assert.IsTrue(solved);
       
        var tasks = _project.RootTask.Children.ToList();
        Assert.That(tasks.Count, Is.EqualTo(2));
        
        Assert.That(tasks[0].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 10)));
        Assert.That(tasks[0].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 30)));
        
        Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 1)));
        // S = 3 рабочих дня (при Confidence по умолчанию 0, см. TShirtType.ToDays):
        // 01.01 (чт) и 02.01 (пт) - первые два, 03-04.01 - выходные, 05.01
        // (пн) - третий. Раньше здесь ожидали 09.01 - это было ДО фикса
        // "Fixed bug with non-working days constraints stretching task
        // duration" (12ca9b9): тогда пересечение с нерабочими днями иногда
        // некорректно растягивало длительность, отсюда и подавленная
        // проверка "fix later". Сам солвер это уже чинили; тест просто не
        // актуализировали.
        Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 5)));
    }

    // Ни одной Fact-записи не задано на входе - весь факт (Started/Completed/
    // InProgress) солвер дописывает сам в PostFillAfterSolve, глядя только на
    // ProjectStart/FactDate и посчитанные им же Plan-даты (см. комментарий
    // там же). Здесь это проверяется явно на двух задачах: одна должна
    // оказаться полностью в прошлом относительно даты среза, другая -
    // как раз "на дату среза" в работе.
    [Test]
    public void FactDateShouldAutoFillCompletedAndInProgressTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            FactDate = new DateOnly(2026, 1, 20),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "short - should be finished by FactDate",
                        Limit = new TaskLimitDto { Duration = 5, ResourceRole = "dev" }
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "long - FactDate should land inside it",
                        Limit = new TaskLimitDto { Duration = 20, ResourceRole = "dev", PredecessorIds = ["2"] }
                    }
                ]
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        var solved = _solver.Solve(_project);
        Assert.IsTrue(solved);

        var task2 = _project.RootTask.Children.First(t => t.Id == "2");
        var task3 = _project.RootTask.Children.First(t => t.Id == "3");

        // задача 2 (05.01 -> 09.01) целиком закончилась раньше даты среза
        // (20.01) - помечена завершённой.
        Assert.That(task2.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 5)));
        Assert.That(task2.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 9)));
        Assert.That(task2.Fact!.IsFinished, Is.True);
        Assert.That(task2.Fact.Records.Select(r => r.Type), Is.EqualTo(new[]
        {
            TaskFactRecordType.Started,
            TaskFactRecordType.Completed
        }));
        Assert.That(task2.Fact.Records[1].RecordedAt, Is.EqualTo(task2.Plan.PlannedFinish));

        // задача 3 (10.01 -> 06.02) - дата среза (20.01) попадает внутрь её
        // планового интервала: значит "в работе", а не "завершена", и с
        // пересчитанным (а не исходной оценкой в 20 дней) остатком.
        Assert.That(task3.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 10)));
        Assert.That(task3.Fact!.IsFinished, Is.False);
        Assert.That(task3.Fact.IsProgress, Is.True);
        Assert.That(task3.Fact.Records.Select(r => r.Type), Is.EqualTo(new[]
        {
            TaskFactRecordType.Started,
            TaskFactRecordType.InProgress
        }));

        var lastRecord = task3.Fact.Records[1];
        Assert.That(lastRecord.RecordedAt, Is.EqualTo(_project.FactDate));
        Assert.That(lastRecord.ResourceName, Is.EqualTo("john"));
        // исходная оценка - 20 рабочих дней; между стартом (10.01, суббота -
        // считается с первого рабочего дня) и датой среза (20.01) прошло 7
        // рабочих дней (см. CalendarLogic.CalcWorkingDaysCount) - остаток
        // пересчитан как 20 - 7 = 13, а не просто скопирован с исходной оценки.
        Assert.That(lastRecord.Duration, Is.EqualTo(13));
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

    // Настоящее "перепланирование": солвер гоняется ДВАЖДЫ на одном и том же
    // ProjectDto - как если бы project.json прочитали, дописали в него
    // Fact/Plan первым прогоном, а потом (спустя полтора месяца, с более
    // поздней FactDate) прочитали и прогнали снова. Проверяем: то, что уже
    // стало Completed после первого прогона, второй не трогает вообще; то,
    // что было InProgress и теперь (по новой FactDate) должно было
    // закончиться - корректно становится Completed, не теряя историю
    // (Started/InProgress остаются в Records, Completed добавляется поверх).
    [Test]
    public void SecondSolveShouldFinalizeTaskThatFinishedSinceFirstRunTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            FactDate = new DateOnly(2026, 1, 20),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto
                    {
                        Id = "short",
                        Name = "short - finished by FactDate #1 already",
                        Limit = new TaskLimitDto { Duration = 5, ResourceRole = "dev" }
                    },
                    new TaskDto
                    {
                        Id = "long",
                        Name = "long - in progress at FactDate #1",
                        Limit = new TaskLimitDto { Duration = 20, ResourceRole = "dev", PredecessorIds = ["short"] }
                    }
                ]
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));

        var shortTask = _project.RootTask.Children.First(t => t.Id == "short");
        var longTask = _project.RootTask.Children.First(t => t.Id == "long");

        // после первого прогона: "short" завершена, "long" в работе с
        // пересчитанным остатком (те же значения, что и в
        // FactDateShouldAutoFillCompletedAndInProgressTest выше).
        Assert.That(shortTask.Fact!.IsFinished, Is.True);
        Assert.That(longTask.Fact!.IsProgress, Is.True);
        Assert.That(longTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 2, 6)));

        var shortPlanAfterRun1 = shortTask.Plan;
        var shortFactAfterRun1 = shortTask.Fact;

        // "перепланирование" - дата среза уходит далеко вперёд (за плановое
        // окончание "long"), солвер прогоняется ЕЩЁ РАЗ на том же ProjectDto.
        _project.FactDate = new DateOnly(2026, 3, 1);
        Assert.IsTrue(_solver.Solve(_project));

        // "short" (уже Fact.IsFinished) во втором прогоне не участвует в
        // модели вообще (CanSkipTask) - Plan и Fact должны остаться теми же
        // самыми объектами, до последнего поля.
        Assert.That(shortTask.Plan, Is.SameAs(shortPlanAfterRun1));
        Assert.That(shortTask.Fact, Is.SameAs(shortFactAfterRun1));
        Assert.That(shortTask.Fact!.Records, Has.Count.EqualTo(2));

        // "long" была в работе, а по новой (гораздо более поздней) дате
        // среза её плановое окончание уже в прошлом - должна стать Completed,
        // сохранив всю историю из первого прогона, а не перезаписав её.
        Assert.That(longTask.Fact!.IsFinished, Is.True);
        Assert.That(longTask.Fact.Records.Select(r => r.Type), Is.EqualTo(new[]
        {
            TaskFactRecordType.Started,
            TaskFactRecordType.InProgress,
            TaskFactRecordType.Completed
        }));
        // плановое окончание не "уехало" от пересчёта на новом FactDate -
        // 20 рабочих дней от 10.01 и 13 (= 20 - 7 уже прошедших) рабочих
        // дней от зафиксированного 21.01 математически дают одну и ту же
        // дату - солвер самосогласован между прогонами.
        Assert.That(longTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 2, 6)));
        Assert.That(longTask.Fact.Records[2].RecordedAt, Is.EqualTo(longTask.Plan.PlannedFinish));
    }

    // "old" уже когда-то выполнена (как DEMO-050 в демо-проекте) - Fact.IsFinished,
    // поэтому у солвера для неё вообще нет интервала в модели (CanSkipTask),
    // а значит формально john "свободен" с самого ProjectStart. "new" -
    // совсем свежая задача без всякого факта, только что заведённая в план;
    // без ограничения на FactDate солвер вполне мог бы поставить её именно
    // в это календарно прошедшее "окно" - результат физически бессмысленный
    // (задача не могла начаться раньше, чем её вообще придумали).
    [Test]
    public void FreshTaskShouldNotBackdateIntoGapLeftByAlreadyCompletedTaskTest()
    {
        _project = new ProjectDto
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
                        Name = "already completed history, like DEMO-050",
                        Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev" },
                        Plan = new TaskPlanDto
                        {
                            ResourceName = "john",
                            PlannedStart = new DateOnly(2026, 1, 5),
                            PlannedFinish = new DateOnly(2026, 1, 7)
                        },
                        Fact = new TaskFactDto
                        {
                            Records =
                            [
                                new TaskFactRecordDto
                                {
                                    RecordedAt = new DateOnly(2026, 1, 7),
                                    Type = TaskFactRecordType.Completed,
                                    ResourceName = "john"
                                }
                            ]
                        }
                    },
                    new TaskDto
                    {
                        Id = "new",
                        Name = "just added to the backlog, no Fact/Plan at all",
                        Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev" }
                    }
                ]
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));

        var newTask = _project.RootTask.Children.First(t => t.Id == "new");

        Assert.That(newTask.Plan!.PlannedStart, Is.GreaterThanOrEqualTo(_project.FactDate));
    }

    // По плану/факту 15.01 оставалось 8 дней (обычный автоматический
    // пересчёт), но 22.01 кто-то вручную скорректировал остаток на 15 дней
    // (например, вскрылась недооценённая сложность) - CorrectedDuration
    // должна победить как "последняя запись прогресса" (см.
    // Solver.CreateTasksIntervals: lastProgressRec ищет среди Started,
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
}
