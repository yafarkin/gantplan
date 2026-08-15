using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;

namespace Tests;

public class TaskAlignmentTests
{
    private static readonly Dictionary<int, long> DefaultWeights = new() { { 0, 1 } };

    private static ResourceDto DevResource(string name = "john") => new()
    {
        Role = "dev",
        Name = name
    };

    private static TaskLimitDto SimpleLimit(params string[] predecessorIds) => new()
    {
        TShirt = TShirtType.S,
        ResourceRole = "dev",
        PredecessorIds = predecessorIds
    };

    // Обязательный тег (RequiredTaskTags), выставленный на корне - дальше
    // наследуется всем листьям, так что отдельные задачи в тестах ниже его
    // не задают.
    private static Dictionary<string, string> RequiredTags() => new()
    {
        [TaskTagKeys.IsOkr] = "false"
    };

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
                Tags = RequiredTags(),
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
                                Limit = SimpleLimit()
                            }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "simple other child task",
                        Limit = SimpleLimit()
                    }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        var originalTask = taskAlignment.OriginalTasks["4"];
        var copyTask = taskAlignment.FlattenTasksCopy["4"];
        var solveCopyTaskExist = taskAlignment.FlattenTasksToSolve.ContainsKey("4");

        Assert.That(originalTask.Disabled, Is.True);
        Assert.That(copyTask.Disabled, Is.True);
        Assert.That(solveCopyTaskExist, Is.False);
    }

    [Test]
    public void DependencyOnGroupShouldExpandToAllItsLeavesTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = RequiredTags(),
                Children = [
                    new TaskDto
                    {
                        Id = "A",
                        Name = "group A",
                        Children = [
                            new TaskDto { Id = "A1", Name = "A1", Limit = SimpleLimit() },
                            new TaskDto { Id = "A2", Name = "A2", Limit = SimpleLimit() }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "X",
                        Name = "depends on the whole group A",
                        Limit = SimpleLimit("A")
                    }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        var resolved = taskAlignment.FlattenTasksCopy["X"].Limit!.PredecessorIds;

        Assert.That(resolved, Is.EquivalentTo(new[] { "A1", "A2" }));
    }

    [Test]
    public void DependencyOnGroupShouldExcludeSkippableLeavesTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = RequiredTags(),
                Children = [
                    new TaskDto
                    {
                        Id = "A",
                        Name = "group A",
                        Children = [
                            new TaskDto { Id = "A1", Name = "A1", Limit = SimpleLimit() },
                            new TaskDto { Id = "A2", Name = "A2", Disabled = true, Limit = SimpleLimit() }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "X",
                        Name = "depends on the whole group A",
                        Limit = SimpleLimit("A")
                    }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        var resolved = taskAlignment.FlattenTasksCopy["X"].Limit!.PredecessorIds;

        Assert.That(resolved, Is.EquivalentTo(new[] { "A1" }));
    }

    [Test]
    public void MixedLeafAndGroupPredecessorsShouldResolveTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = RequiredTags(),
                Children = [
                    new TaskDto { Id = "Y", Name = "Y", Limit = SimpleLimit() },
                    new TaskDto
                    {
                        Id = "A",
                        Name = "group A",
                        Children = [
                            new TaskDto { Id = "A1", Name = "A1", Limit = SimpleLimit() }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "X",
                        Name = "depends on Y and the whole group A",
                        Limit = SimpleLimit("Y", "A")
                    }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        var resolved = taskAlignment.FlattenTasksCopy["X"].Limit!.PredecessorIds;

        Assert.That(resolved, Is.EquivalentTo(new[] { "Y", "A1" }));
    }

    [Test]
    public void TagsShouldBeInheritedButOverridableTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string>
                {
                    [TaskTagKeys.WorkType] = WorkType.Business.ToString(),
                    [TaskTagKeys.IsOkr] = "false"
                },
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "inherits WorkType from root",
                        Limit = SimpleLimit()
                    },
                    new TaskDto
                    {
                        Id = "3",
                        Name = "overrides WorkType",
                        Tags = new Dictionary<string, string> { [TaskTagKeys.WorkType] = WorkType.Team.ToString() },
                        Limit = SimpleLimit()
                    }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        Assert.That(taskAlignment.FlattenTasksCopy["2"].Tags![TaskTagKeys.WorkType], Is.EqualTo(WorkType.Business.ToString()));
        Assert.That(taskAlignment.FlattenTasksCopy["3"].Tags![TaskTagKeys.WorkType], Is.EqualTo(WorkType.Team.ToString()));
    }

    // Ребёнок вообще не задал Tags - должен получить полную копию тегов родителя.
    [Test]
    public void ChildWithNoTagsShouldGetFullCopyOfParentTagsTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string>
                {
                    [TaskTagKeys.WorkType] = WorkType.Business.ToString(),
                    [TaskTagKeys.IsOkr] = "true"
                },
                Children = [
                    new TaskDto { Id = "2", Name = "no own tags at all", Limit = SimpleLimit() }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        var tags = taskAlignment.FlattenTasksCopy["2"].Tags;

        Assert.That(tags, Is.Not.Null);
        Assert.That(tags, Has.Count.EqualTo(2));
        Assert.That(tags![TaskTagKeys.WorkType], Is.EqualTo(WorkType.Business.ToString()));
        Assert.That(tags[TaskTagKeys.IsOkr], Is.EqualTo("true"));
    }

    // Родитель задал только WorkType, ребёнок - только IsOkr: у ребёнка в
    // итоге должны оказаться оба тега (наследование - это merge, а не
    // замена/перетирание уже заданных у ребёнка ключей).
    [Test]
    public void DifferentTagsOnParentAndChildShouldMergeTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = new Dictionary<string, string> { [TaskTagKeys.WorkType] = WorkType.Business.ToString() },
                Children = [
                    new TaskDto
                    {
                        Id = "2",
                        Name = "sets only IsOkr",
                        Tags = new Dictionary<string, string> { [TaskTagKeys.IsOkr] = "true" },
                        Limit = SimpleLimit()
                    }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        var tags = taskAlignment.FlattenTasksCopy["2"].Tags;

        Assert.That(tags, Has.Count.EqualTo(2));
        Assert.That(tags![TaskTagKeys.WorkType], Is.EqualTo(WorkType.Business.ToString()), "тег, унаследованный от родителя");
        Assert.That(tags[TaskTagKeys.IsOkr], Is.EqualTo("true"), "собственный тег ребёнка не должен перетереться");
    }

    [Test]
    public void MissingRequiredTagShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                // IsOkr (RequiredTaskTags) нигде не задан - ни на корне, ни на задаче
                Limit = SimpleLimit()
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Tag 'IsOkr' is not set for task 1"));
    }

    [Test]
    public void MissingResourceShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Limit = new TaskLimitDto { TShirt = TShirtType.S } // no ResourceName/ResourceRole
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Can't find resource for task 1"));
    }

    [Test]
    public void WeightsWithoutDefaultPriorityShouldFailTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Limit = SimpleLimit()
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, new Dictionary<int, long>()));

        Assert.That(ex.Message, Is.EqualTo("Weights must contain a default entry for priority 0"));
    }

    [Test]
    public void UnknownPredecessorShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Limit = SimpleLimit("does-not-exist")
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("refers to unknown predecessor does-not-exist"));
    }

    [Test]
    public void CircularDependencyShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Children = [
                    new TaskDto { Id = "2", Name = "2", Limit = SimpleLimit("3") },
                    new TaskDto { Id = "3", Name = "3", Limit = SimpleLimit("2") }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Circular dependency"));
    }

    [Test]
    public void MultipleProblemsShouldBeReportedTogetherTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Children = [
                    new TaskDto { Id = "2", Name = "no resource", Limit = new TaskLimitDto { TShirt = TShirtType.S } },
                    new TaskDto { Id = "3", Name = "unknown predecessor", Limit = SimpleLimit("missing") }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Can't find resource for task 2"));
        Assert.That(ex.Message, Does.Contain("refers to unknown predecessor missing"));
    }
}
