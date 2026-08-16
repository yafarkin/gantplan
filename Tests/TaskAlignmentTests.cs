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

    // У одного человека два периода NonWorkingDays пересекаются на одну и ту
    // же дату - в отличие от глобального календаря (где пересечение разных
    // источников - штатный случай, см. CalendarLogic), у одного ресурса это
    // почти наверняка ошибка в данных (дубль записи или опечатка в дате), а
    // не два независимых легитимных периода - поэтому здесь ошибка, а не
    // молчаливое слияние.
    [Test]
    public void OverlappingResourceCalendarPeriodsShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = RequiredTags(),
                Limit = SimpleLimit()
            },
            Resources =
            [
                new ResourceDto
                {
                    Role = "dev",
                    Name = "john",
                    Calendar = new CalendarDto
                    {
                        NonWorkingDays =
                        [
                            new CalendarPeriod { From = new DateOnly(2026, 1, 5), To = new DateOnly(2026, 1, 10) },
                            new CalendarPeriod { From = new DateOnly(2026, 1, 8), To = new DateOnly(2026, 1, 12) }
                        ]
                    }
                }
            ]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("john"));
        Assert.That(ex.Message, Does.Contain("NonWorkingDays"));
    }

    // То же самое, но у WorkingDays (например, две отдельные "рабочие
    // отработки" одного человека, которые перекрылись) - проверяем оба
    // списка периодов, не только NonWorkingDays.
    [Test]
    public void OverlappingResourceWorkingDaysPeriodsShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = RequiredTags(),
                Limit = SimpleLimit()
            },
            Resources =
            [
                new ResourceDto
                {
                    Role = "dev",
                    Name = "john",
                    Calendar = new CalendarDto
                    {
                        WorkingDays =
                        [
                            new CalendarPeriod { From = new DateOnly(2026, 1, 5), To = new DateOnly(2026, 1, 10) },
                            new CalendarPeriod { From = new DateOnly(2026, 1, 8), To = new DateOnly(2026, 1, 12) }
                        ]
                    }
                }
            ]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("john"));
        Assert.That(ex.Message, Does.Contain("WorkingDays"));
    }

    // Пересечение NonWorkingDays и WorkingDays у одного человека - это НЕ
    // ошибка, а штатный способ переопределить чужое правило для конкретных
    // дат (например, глобально команда в командировке, а WorkingDays у
    // человека явно говорит "а я в эти дни работаю") - здесь проверяем, что
    // ложного срабатывания нет, только пересечения ВНУТРИ одного и того же
    // списка считаются ошибкой.
    [Test]
    public void NonWorkingAndWorkingDaysOverlapOnSameResourceShouldNotFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = RequiredTags(),
                Limit = SimpleLimit()
            },
            Resources =
            [
                new ResourceDto
                {
                    Role = "dev",
                    Name = "john",
                    Calendar = new CalendarDto
                    {
                        NonWorkingDays = [new CalendarPeriod { From = new DateOnly(2026, 1, 5), To = new DateOnly(2026, 1, 10) }],
                        WorkingDays = [new CalendarPeriod { From = new DateOnly(2026, 1, 8), To = new DateOnly(2026, 1, 9) }]
                    }
                }
            ]
        };

        var taskAlignment = new TaskAlignment();
        Assert.DoesNotThrow(() => taskAlignment.Alignment(project, DefaultWeights));
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

    // Цикл длиной 3 (2 -> 3 -> 4 -> 2), а не просто пара взаимных ссылок -
    // наивная проверка "A ссылается на B, и B ссылается на A" такой цикл не
    // поймает, нужен честный обход графа (см. ProjectValidator.HasCycle).
    [Test]
    public void CircularDependencyAcrossThreeTasksShouldFailValidationTest()
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
                    new TaskDto { Id = "3", Name = "3", Limit = SimpleLimit("4") },
                    new TaskDto { Id = "4", Name = "4", Limit = SimpleLimit("2") }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Circular dependency"));
        // в сообщении должны быть все три задачи цикла, а не только пара
        // соседних - иначе тест не отличил бы честный обход трёх узлов от
        // случайного совпадения на двух.
        Assert.That(ex.Message, Does.Contain("2"));
        Assert.That(ex.Message, Does.Contain("3"));
        Assert.That(ex.Message, Does.Contain("4"));
    }

    // X зависит от ЦЕЛОЙ группы A (её id, не id листа), а лист A1 внутри неё
    // зависит от X - цикл X -> A1 -> X существует только на уровне листьев,
    // и в PredecessorIds задачи X буквально написано "A", а не "A1". Если бы
    // проверка циклов сравнивала id как есть, без разворачивания группы в
    // листья (см. ProjectValidator.HasCycle -> treeIndex.ExpandToLeafIds),
    // она бы этот цикл просто не увидела.
    [Test]
    public void CircularDependencyThroughGroupExpansionShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Children = [
                    new TaskDto
                    {
                        Id = "A",
                        Name = "group A",
                        Children = [
                            new TaskDto { Id = "A1", Name = "A1", Limit = SimpleLimit("X") }
                        ]
                    },
                    new TaskDto { Id = "X", Name = "X", Limit = SimpleLimit("A") }
                ]
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Circular dependency"));
        Assert.That(ex.Message, Does.Contain("X"));
        Assert.That(ex.Message, Does.Contain("A1"));
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
