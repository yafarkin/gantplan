using GantPlan.Dtos;
using GantPlan.Logic;

namespace Tests;

public sealed class NoTasksTests
{
    private ProjectDto _project;
    private Solver _solver;
    
    [SetUp]
    public void Setup()
    {
        _solver = new Solver();
    }

    [Test]
    public void EmptyProjectTest()
    {
        _project = new ProjectDto();
        
        var ex = Assert.Throws<Exception>(() => _solver.Solve(_project));
        Assert.That(ex.Message, Is.EqualTo("Root task cannot be null"));
    }

    [Test]
    public void AllTaskCompletedOrDisabledTests()
    {
        _project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new  TaskDto
            {
                Id = "1",
                Name = "root task",
                Children = [
                    new TaskDto
                    {
                        Id ="2",
                        Name = "child task",
                        Disabled = true,
                        Limit = new TaskLimitDto
                        {
                            Duration = 1
                        }
                    },
                    new TaskDto
                    {
                        Id ="3",
                        Name = "other child task",
                        Limit = new  TaskLimitDto
                        {
                            Duration = 1
                        },
                        Fact = new  TaskFactDto
                        {
                            Records =
                            [
                                new()
                                {
                                    RecordedAt = new DateOnly(2026, 1, 1),
                                    Type = TaskFactRecordType.Completed
                                }
                            ]
                        }
                    }
                ]
            }
        };
        
        var ex = Assert.Throws<Exception>(() => _solver.Solve(_project));
        Assert.That(ex.Message, Is.EqualTo("No tasks found"));
    }
}