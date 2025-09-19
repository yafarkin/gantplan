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
        Assert.IsNotNull(tasks[0].Fact);
        Assert.That(tasks[0].Fact.IsProgress, Is.True);
        Assert.That(tasks[0].Fact.IsFinished, Is.False);
        Assert.That(tasks[0].Fact.StartDate, Is.EqualTo(tasks[0].Plan!.PlannedStart));
        Assert.That(tasks[0].Fact.FinishDate, Is.Null);
        Assert.That(tasks[0].Fact.Records.Count, Is.EqualTo(1));
        Assert.That(tasks[0].Fact.Records[0].RecordedAt, Is.EqualTo(tasks[0].Plan!.PlannedStart));
        Assert.That(tasks[0].Fact.Records[0].Duration, Is.EqualTo(tasks[0].Limit!.TShirt!.Value.ToDays()));
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
        Assert.That(tasks[1].Fact.Records[0].Duration, Is.EqualTo(tasks[1].Limit!.TShirt!.Value.ToDays()));
        Assert.That(tasks[1].Fact.Records[0].ResourceName, Is.EqualTo(expectedResource1));
        Assert.That(tasks[1].Fact.Records[0].Type, Is.EqualTo(TaskFactRecordType.Started));
        
        Assert.That(tasks[2].Plan!.ResourceName, Is.EqualTo(expectedResource2));
        Assert.That(tasks[2].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 1)));
        Assert.That(tasks[2].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 5)));
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
        //Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 9))); // fix later
    }
}