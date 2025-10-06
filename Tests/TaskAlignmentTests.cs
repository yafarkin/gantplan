using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;

namespace Tests;

public class TaskAlignmentTests
{
    [Test]
    public void DisabledShouldDescendToChildTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "simple task",
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "simple child task",
                        Disabled = true,
                        Children = [
                            new TaskDto
                            {
                                Id = "4",
                                Name = "simple child task (second level)",
                                Limit = new TaskLimitDto
                                {
                                    TShirt = TShirtType.S,
                                    ResourceRole = "dev"
                                }
                            }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "simple other child task",
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
                }
            }
        };
        
        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, new Dictionary<int, long>());

        var originalTask = taskAlignment.OriginalTasks["4"];
        var copyTask = taskAlignment.FlattenTasksCopy["4"];
        var solveCopyTaskExist = taskAlignment.FlattenTasksToSolve.ContainsKey("4");
        
        Assert.That(originalTask.Disabled, Is.True);
        Assert.That(copyTask.Disabled, Is.True);
        Assert.That(solveCopyTaskExist, Is.False);
    }
}