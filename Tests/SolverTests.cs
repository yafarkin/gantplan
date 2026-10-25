using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;

namespace Tests;

public sealed class SolverTests
{
    private ProjectDto _project = null!;
    private Solver _solver = null!;
    
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
        var expectedResource2 = _project.Resources.Last().Name;

        var tasks = _project.RootTask.Children.ToList();
        
        Assert.That(tasks[0].Plan!.ResourceName, Is.EqualTo(expectedResource1));
        Assert.That(tasks[0].Plan!.PlannedStart, Is.EqualTo(new  DateOnly(2026, 01, 15)));
        Assert.That(tasks[0].Plan!.PlannedFinish, Is.EqualTo(new  DateOnly(2026, 01, 23)));
        // без FactDate солвер факт не трогает: ни старт, ни прогресс не
        // дописываются (раньше задачам "в будущем" ставился Started).
        Assert.That(tasks[0].Fact, Is.Null);
        
        Assert.That(tasks[1].Plan!.ResourceName, Is.EqualTo(expectedResource1));
        // task1 заканчивается в пятницу 23.01 - следующая начинается не в
        // субботу 24.01, а в понедельник 26.01 (старт в нерабочий день запрещён).
        Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 26)));
        Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 28)));
        // без FactDate солвер факт не трогает: ни старт, ни прогресс не
        // дописываются (раньше задачам "в будущем" ставился Started).
        Assert.That(tasks[1].Fact, Is.Null);
        
        Assert.That(tasks[2].Plan!.ResourceName, Is.EqualTo(expectedResource2));
        Assert.That(tasks[2].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 1)));
        Assert.That(tasks[2].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 3)));
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
    

    // DueDate включительно (как и AvailTo): задача может закончиться в сам
    // день срока. 5 рабочих дней с понедельника 05.01 - ровно до пятницы
    // 09.01 = DueDate; раньше срок считался исключающим, и такая модель
    // была невыполнимой.
    [Test]
    public void TaskMayFinishOnDueDateItselfTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Limit = new TaskLimitDto { Duration = 5, ResourceRole = "dev", DueDate = new DateOnly(2026, 1, 9) }
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));
        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 9)));
        Assert.That(_solver.Warnings, Is.Empty);
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
        
        // StartAfter = суббота 10.01 - старт в первый рабочий день после неё.
        Assert.That(tasks[0].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 12)));
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

    // Горизонт считается под проект (см. Solver.CalcHorizon): раньше он был
    // зашит в 180 дней, и проект длиннее полугода молча не решался.
    [Test]
    public void ProjectLongerThanHalfYearShouldBeSolvedTest()
    {
        var children = Enumerable.Range(0, 10)
            .Select(i => new TaskDto
            {
                Id = $"t{i}",
                Name = $"t{i}",
                Limit = new TaskLimitDto
                {
                    Duration = 30,
                    ResourceRole = "dev",
                    PredecessorIds = i == 0 ? [] : [$"t{i - 1}"]
                }
            })
            .ToList();

        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = children
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));
        Assert.That(_solver.Horizon, Is.GreaterThan(180));

        // 300 рабочих дней подряд с понедельника 05.01.2026 - ровно 60
        // рабочих недель, последний день - пятница 59-й недели после старта.
        var last = _project.RootTask.Children.Single(t => t.Id == "t9");
        Assert.That(last.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 5).AddDays(59 * 7 + 4)));
    }

    // Между равноценными планами (одинаковая цель) солвер должен выбирать
    // однозначно (см. Solver.SolveTieBreak): раньше стартует задача, которая
    // раньше в дереве, а первому в списке исполнителю достаётся первая задача.
    [TestCase(1)]
    [TestCase(2)]
    public void EquivalentPlansShouldBeResolvedByTreeOrderTest(int resourceCount)
    {
        var names = new[] { "john", "doe" };

        for (var run = 0; run < 3; run++)
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
                        new TaskDto { Id = "a", Name = "a", Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev" } },
                        new TaskDto { Id = "b", Name = "b", Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev" } },
                        new TaskDto { Id = "c", Name = "c", Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev" } }
                    ]
                },
                Resources = names.Take(resourceCount).Select(n => new ResourceDto { Role = "dev", Name = n }).ToList()
            };

            Assert.IsTrue(new Solver().Solve(_project));

            var tasks = _project.RootTask.Children.ToList();
            if (resourceCount == 1)
            {
                Assert.That(tasks.Select(t => t.Plan!.PlannedStart), Is.Ordered.Ascending);
                Assert.That(tasks.Select(t => t.Plan!.PlannedStart).Distinct().Count(), Is.EqualTo(3));
            }
            else
            {
                Assert.That(tasks[0].Plan!.ResourceName, Is.EqualTo("john"));
                Assert.That(tasks[1].Plan!.ResourceName, Is.EqualTo("doe"));
                Assert.That(tasks[0].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 5)));
                Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 5)));
            }
        }
    }

    // Неполная загрузка (Percent) округляется вверх: 5 дней при 80% - это
    // 6.25, то есть 7 рабочих дней, а не 6.
    [Test]
    public void PartialPercentShouldRoundDurationUpTest()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 5),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Limit = new TaskLimitDto { Duration = 5, ResourceRole = "dev" }
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john", Percent = 80 }]
        };

        Assert.IsTrue(_solver.Solve(_project));
        // 05.01-09.01 - пять дней, 12.01 и 13.01 - шестой и седьмой.
        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 13)));
    }
}
