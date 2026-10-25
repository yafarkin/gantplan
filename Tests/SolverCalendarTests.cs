using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;

namespace Tests;

public sealed class SolverCalendarTests
{
    private ProjectDto _project = null!;
    private Solver _solver = null!;
    
    [SetUp]
    public void Setup()
    {
        _solver = new Solver();
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
        // doe доступен с субботы 03.01, но начинает в первый рабочий день - 05.01.
        Assert.That(tasks[1].Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 01, 5)));
        Assert.That(tasks[1].Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 01, 8)));
    }

    // Солвер не должен ставить старт или окончание задачи на нерабочий день:
    // раньше старт в субботу был для него "выгоднее" понедельника (конец тот
    // же, а start в целевой функции меньше), и на диаграмме задачи
    // начинались в выходные.
    [Test]
    public void NoTaskShouldStartOrFinishOnNonWorkingDayTest()
    {
        var holiday = new DateOnly(2026, 1, 14);

        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            GlobalCalendar = new CalendarDto
            {
                NonWorkingDays = [new CalendarPeriod { From = holiday, To = holiday }]
            },
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "false" },
                Children = [
                    new TaskDto { Id = "a", Name = "a", Limit = new TaskLimitDto { Duration = 2, ResourceRole = "dev" } },
                    new TaskDto { Id = "b", Name = "b", Limit = new TaskLimitDto { Duration = 3, ResourceRole = "dev", PredecessorIds = ["a"] } },
                    new TaskDto { Id = "c", Name = "c", Limit = new TaskLimitDto { Duration = 1, ResourceRole = "dev", PredecessorIds = ["b"] } },
                    new TaskDto { Id = "d", Name = "d", Limit = new TaskLimitDto { Duration = 4, ResourceRole = "dev", PredecessorIds = ["c"] } }
                ]
            },
            Resources = [new ResourceDto { Role = "dev", Name = "john" }]
        };

        Assert.IsTrue(_solver.Solve(_project));

        static bool IsNonWorking(DateOnly d, DateOnly holiday) =>
            d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || d == holiday;

        foreach (var task in _project.RootTask.Children)
        {
            Assert.That(IsNonWorking(task.Plan!.PlannedStart!.Value, holiday), Is.False, $"start of {task.Id}");
            Assert.That(IsNonWorking(task.Plan!.PlannedFinish!.Value, holiday), Is.False, $"finish of {task.Id}");
        }

        // a: 01.01 (чт) - 02.01 (пт); b: 05.01 - 07.01; c: 08.01;
        // d: 09.01 (пт), 12.01, 13.01, 15.01 (14.01 - праздник).
        var d = _project.RootTask.Children.Single(t => t.Id == "d");
        Assert.That(d.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 9)));
        Assert.That(d.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 15)));
    }

    // AvailTo - последний доступный день включительно (так его понимают и
    // экспортёры отчётов). Раньше солвер занимал ресурс уже с самого дня
    // AvailTo, и задача ровно до этого дня включительно не помещалась.
    [Test]
    public void ResourceShouldBeAvailableOnAvailToDayTest()
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
            Resources = [new ResourceDto { Role = "dev", Name = "john", AvailTo = new DateOnly(2026, 1, 9) }]
        };

        Assert.IsTrue(_solver.Solve(_project));
        Assert.That(_project.RootTask.Plan!.PlannedStart, Is.EqualTo(new DateOnly(2026, 1, 5)));
        Assert.That(_project.RootTask.Plan!.PlannedFinish, Is.EqualTo(new DateOnly(2026, 1, 9)));
    }
}
