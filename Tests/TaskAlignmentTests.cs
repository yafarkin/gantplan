using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;
using static Tests.TaskTestData;

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

    // То же самое, что и предыдущий тест, но исключение через Fact.IsPaused,
    // а не Disabled - TaskDto.CanSkipTask объединяет оба условия через ||,
    // но до сих пор тестами было покрыто только Disabled; надо убедиться,
    // что настоящая "пауза" (запись факта, а не булевый флаг) работает
    // точно так же - и исключается из решения, и вырезается из чужих
    // PredecessorIds при разворачивании зависимости от группы.
    [Test]
    public void DependencyOnGroupShouldExcludePausedLeavesTest()
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
                            new TaskDto
                            {
                                Id = "A2",
                                Name = "A2 - paused",
                                Limit = SimpleLimit(),
                                Fact = new TaskFactDto
                                {
                                    Records = [new TaskFactRecordDto { RecordedAt = new DateOnly(2026, 1, 1), Type = TaskFactRecordType.Paused }]
                                }
                            }
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

        // A2 на паузе - не должна попасть в список задач "на решение"...
        Assert.That(taskAlignment.FlattenTasksToSolve.ContainsKey("A2"), Is.False);
        Assert.That(taskAlignment.FlattenTasksToSolve.ContainsKey("A1"), Is.True);

        // ...и должна быть вырезана из PredecessorIds задачи, зависящей от
        // ВСЕЙ группы A, как и Disabled-лист в тесте выше.
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

    // Зависимость от группы A, у которой сама A1/A2 не прямые дети, а лежат
    // ещё на уровень глубже, внутри подгруппы - TaskTreeIndex.CollectLeafIds
    // рекурсивный и должен развернуть "depends on A" до его настоящих
    // листьев независимо от того, на какой глубине они лежат.
    [Test]
    public void DependencyOnNestedGroupShouldExpandToDeeplyNestedLeavesTest()
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
                            new TaskDto
                            {
                                Id = "A-sub",
                                Name = "subgroup inside A",
                                Children = [
                                    new TaskDto { Id = "A1", Name = "A1", Limit = SimpleLimit() },
                                    new TaskDto { Id = "A2", Name = "A2", Limit = SimpleLimit() }
                                ]
                            }
                        ]
                    },
                    new TaskDto
                    {
                        Id = "X",
                        Name = "depends on the whole (nested) group A",
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

    // Тег задан только на корне (уровень 1), а не на промежуточном уровне
    // (уровень 2) - должен всё равно долететь до листа на уровне 3. Уровень
    // 2 при этом задаёт свой собственный, третий тег - так видно, что это
    // настоящий каскад через два хопа (Apply мутирует Tags родителя ПЕРЕД
    // тем, как спускаться к его детям, и на уровне 2 использует уже
    // домёрженные с корнем теги), а не просто прямое копирование родитель-
    // ребёнок, которое проверяют тесты выше.
    [Test]
    public void TagsShouldCascadeThroughThreeTreeLevelsTest()
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
                        Name = "middle level - own tag, doesn't set WorkType/IsOkr itself",
                        Tags = new Dictionary<string, string> { ["Component"] = "backend" },
                        Children = [
                            new TaskDto
                            {
                                Id = "3",
                                Name = "leaf two levels below root - no own tags at all",
                                Limit = SimpleLimit()
                            }
                        ]
                    }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        taskAlignment.Alignment(project, DefaultWeights);

        var tags = taskAlignment.FlattenTasksCopy["3"].Tags;

        Assert.That(tags, Is.Not.Null);
        Assert.That(tags, Has.Count.EqualTo(3));
        Assert.That(tags![TaskTagKeys.WorkType], Is.EqualTo(WorkType.Business.ToString()), "тег корня, дошедший через уровень 2");
        Assert.That(tags[TaskTagKeys.IsOkr], Is.EqualTo("false"), "второй тег корня, дошедший через уровень 2");
        Assert.That(tags["Component"], Is.EqualTo("backend"), "собственный тег уровня 2 не потерялся при спуске дальше");
    }
}
