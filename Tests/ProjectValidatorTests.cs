using GantPlan.Dtos;
using GantPlan.Dtos.Enums;
using GantPlan.Logic;
using static Tests.TaskTestData;

namespace Tests;

public class ProjectValidatorTests
{
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
    public void NonPositiveResourcePercentShouldFailValidationTest()
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
            Resources = [new ResourceDto { Role = "dev", Name = "john", Percent = 0 }]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Resource john has invalid Percent 0"));
    }

    [Test]
    public void StartAfterLaterThanDueDateShouldFailValidationTest()
    {
        var project = new ProjectDto
        {
            ProjectStart = new DateOnly(2026, 1, 1),
            RootTask = new TaskDto
            {
                Id = "1",
                Name = "root",
                Tags = RequiredTags(),
                Limit = new TaskLimitDto
                {
                    TShirt = TShirtType.S,
                    ResourceRole = "dev",
                    StartAfter = new DateOnly(2026, 2, 1),
                    DueDate = new DateOnly(2026, 1, 20)
                }
            },
            Resources = [DevResource()]
        };

        var taskAlignment = new TaskAlignment();
        var ex = Assert.Throws<Exception>(() => taskAlignment.Alignment(project, DefaultWeights));

        Assert.That(ex.Message, Does.Contain("Task 1 has StartAfter 2026-02-01 later than DueDate 2026-01-20"));
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
