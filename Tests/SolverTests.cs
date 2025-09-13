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
                            Duration = 3,
                            //TShirt = TShirtType.L,
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
                    // Calendar = new CalendarDto
                    // {
                    //     WorkingDays = new List<CalendarPeriod>
                    //     {
                    //         new()
                    //         {
                    //             From = new DateOnly(2026, 01, 03),
                    //             To = new DateOnly(2026, 01, 04)
                    //         }
                    //     },
                    //     NonWorkingDays = new List<CalendarPeriod>
                    //     {
                    //         new()
                    //         {
                    //             From =  new DateOnly(2026, 01, 05),
                    //             To =  new DateOnly(2026, 01, 05)
                    //         }
                    //     }
                    // }
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
        
        Assert.That(tasks[1].Plan!.ResourceName, Is.EqualTo(expectedResource1));
        Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new  DateOnly(2026, 01, 24)));
        Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new  DateOnly(2026, 01, 28)));
        
        Assert.That(tasks[2].Plan!.ResourceName, Is.EqualTo(expectedResource2));
        Assert.That(tasks[2].Plan!.PlannedStart, Is.EqualTo(new  DateOnly(2026, 01, 1)));
        Assert.That(tasks[2].Plan!.PlannedFinish, Is.EqualTo(new  DateOnly(2026, 01, 20)));
        
    }
}