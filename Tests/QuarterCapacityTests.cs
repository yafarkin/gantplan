using System.Globalization;
using GantPlan.Dtos;
using GantPlan.Visualize;

namespace Tests;

public sealed class QuarterCapacityTests
{
    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly End = Start.AddDays(99);

    private static ProjectDto Project(params (string Name, int Business, int Other)[] people)
    {
        var tasks = new List<TaskDto>();
        foreach (var (name, business, other) in people)
        {
            var cursor = Start;
            if (business > 0)
            {
                tasks.Add(Task($"{name}-biz", name, "Business", cursor, business));
                cursor = cursor.AddDays(business);
            }

            if (other > 0)
            {
                tasks.Add(Task($"{name}-other", name, "Team", cursor, other));
            }
        }

        return new ProjectDto
        {
            ProjectStart = Start,
            GlobalCalendar = new CalendarDto
            {
                WorkingDays = [new CalendarPeriod { From = Start, To = End }]
            },
            Resources = people.Select(p => new ResourceDto { Name = p.Name, Role = "DEV" }).ToArray(),
            RootTask = new TaskDto { Id = "root", Name = "root", Children = tasks }
        };
    }

    private static TaskDto Task(string id, string person, string type, DateOnly start, int days) => new()
    {
        Id = id,
        Name = id,
        Tags = new Dictionary<string, string> { [TaskTagKeys.WorkType] = type },
        Plan = new TaskPlanDto
        {
            ResourceName = person,
            PlannedStart = start,
            PlannedFinish = start.AddDays(days - 1)
        }
    };

    private static (string Quarter, string Daily) Reports(ProjectDto project, double quota = 0.20)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            return (QuarterDistributionExporter.Build(project, Start, End, quota),
                DailyPlanExporter.Build(project, Start, End, quota));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Строка таблицы по человеку: имя дополнено пробелами до ширины колонки.
    private static string QuarterLine(string report, string person) =>
        report.Split('\n').Single(line => line.StartsWith(person + " ", StringComparison.Ordinal)
                                          || line.StartsWith(person + " │", StringComparison.Ordinal));

    private static string DailySection(string report, string person)
    {
        var start = report.IndexOf($"=== {person} ===", StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0));
        var next = report.IndexOf("=== ", start + 4, StringComparison.Ordinal);
        return report[start..(next < 0 ? report.Length : next)];
    }

    [Test]
    public void ReserveIsSubtractedBeforeCalculatingIdleAndPercentages()
    {
        var (quarter, daily) = Reports(Project(("Dev1", 30, 20)));

        var line = QuarterLine(quarter, "Dev1");
        Assert.Multiple(() =>
        {
            // 100 рабочих дней = 20 резерв + 80 ёмкость; 30/80=38%, 20/80=25%, 30/80=38%.
            Assert.That(line, Does.Contain("│  100 │   20 /  20  ok"));
            Assert.That(line, Does.Contain("30  38%"));
            Assert.That(line, Does.Contain("20  25%"));
            Assert.That(QuarterLine(quarter, "ВСЕГО"), Does.Contain("30  38%"));
            Assert.That(quarter, Does.Not.Contain("Требуют внимания"));
            Assert.That(quarter, Does.Contain("Итого: бизнес 30 из 80 дней ёмкости (38%), это 30% от всех 100 рабочих дней."));
            Assert.That(quarter, Does.Contain("Резерв: цель 20 дней, обеспечено 20."));
            Assert.That(daily, Does.Contain("Dev1, итог за период: рабочих дней 100 = 20 резерв + 80 ёмкость."));
            Assert.That(daily, Does.Contain("бизнес 30 (38% ёмкости);  не-бизнес 20 (25%);  свободно сверх резерва 30 (38%)"));
            Assert.That(daily, Does.Contain("резерв обеспечен: 20 из 20."));
        });
    }

    [Test]
    public void FullyUsedReserveShowsOverloadForPersonAndTotal()
    {
        var (quarter, daily) = Reports(Project(("Dev1", 70, 30)));

        Assert.Multiple(() =>
        {
            Assert.That(QuarterLine(quarter, "Dev1"), Does.Contain("20 /   0  -20"));
            Assert.That(QuarterLine(quarter, "ВСЕГО"), Does.Contain("20 /   0  -20"));
            Assert.That(quarter, Does.Contain("Dev1: рабочих дней 100 = 20 резерв + 80 ёмкость."));
            Assert.That(quarter, Does.Contain("Задачами занято 100 дней при ёмкости 80, перебор 20 дней."));
            Assert.That(quarter, Does.Contain("Свободными осталось 0 дней вместо 20 — резерв закрыт на 0%."));
            Assert.That(quarter, Does.Contain("Запаса по команде нет: свободно 0 дней при цели 20."));
            Assert.That(quarter, Does.Contain("Не хватает резерва: Dev1 20."));
            Assert.That(quarter, Does.Not.Contain("Свободно сверх резерва:"));
            Assert.That(daily, Does.Contain("резерв обеспечен: 0 из 20;  НЕ ХВАТАЕТ 20 дн."));
        });
    }

    [Test]
    public void PersonOverloadRemainsVisibleWhenTeamHasFreeCapacity()
    {
        var (quarter, daily) = Reports(Project(("Dev1", 90, 0), ("Dev2", 0, 0)));

        Assert.Multiple(() =>
        {
            Assert.That(QuarterLine(quarter, "Dev1"), Does.Contain("20 /  10  -10"));
            Assert.That(QuarterLine(quarter, "Dev2"), Does.Contain("80 100%"));
            Assert.That(QuarterLine(quarter, "ВСЕГО"), Does.Contain("40 /  30  -10"));
            // Свободных дней по команде больше цели, но лежат они не у того человека.
            Assert.That(quarter, Does.Contain("Запас по команде есть: 110 свободных дней при цели 40, но он не у тех людей."));
            Assert.That(quarter, Does.Contain("Свободно сверх резерва: Dev2 80."));
            Assert.That(quarter, Does.Contain("Не хватает резерва: Dev1 10."));
            Assert.That(daily, Does.Contain("Dev1, итог за период: рабочих дней 100 = 20 резерв + 80 ёмкость."));
            Assert.That(daily, Does.Contain("резерв обеспечен: 10 из 20;  НЕ ХВАТАЕТ 10 дн."));
        });
    }

    [Test]
    public void PeopleWithoutAvailableDaysAreListedSeparately()
    {
        var project = Project(("Dev1", 10, 0), ("Dev2", 0, 0)) with
        {
            Resources =
            [
                new ResourceDto { Name = "Dev1", Role = "DEV" },
                new ResourceDto { Name = "Dev2", Role = "DEV", AvailFrom = End.AddDays(10) }
            ]
        };

        var (quarter, _) = Reports(project);

        Assert.Multiple(() =>
        {
            Assert.That(quarter, Does.Contain("Без доступных дней в периоде: Dev2 (DEV)."));
            Assert.That(quarter.Split('\n'), Has.None.StartsWith("Dev2 "));
        });
    }

    [Test]
    public void RolesComeFromResourcesAndGetTheirOwnSubtotals()
    {
        var project = Project(("Dev1", 30, 0), ("Dev2", 10, 0)) with
        {
            Resources =
            [
                new ResourceDto { Name = "Dev1", Role = "DEV" },
                new ResourceDto { Name = "Dev2", Role = "QA" }
            ]
        };

        var (quarter, daily) = Reports(project);

        Assert.Multiple(() =>
        {
            // Роль берётся из ресурса, фильтр не нужен: каждая роль получает свой ИТОГО.
            Assert.That(QuarterLine(quarter, "Dev1"), Does.Contain("│ DEV  │"));
            Assert.That(QuarterLine(quarter, "Dev2"), Does.Contain("│ QA   │"));
            Assert.That(quarter, Does.Contain("ИТОГО │ DEV  │  100 │"));
            Assert.That(quarter, Does.Contain("ИТОГО │ QA   │  100 │"));
            Assert.That(quarter, Does.Contain("ВСЕГО │ все  │  200 │"));
            Assert.That(daily, Does.Contain("ИТОГО │ QA   │  100 │"));
        });
    }

    // Одна роль на всех - подытог дублировал бы итоговую строку, поэтому его нет.
    [Test]
    public void SingleRoleGetsNoSubtotalRow()
    {
        var (quarter, _) = Reports(Project(("Dev1", 30, 0), ("Dev2", 10, 0)));

        Assert.Multiple(() =>
        {
            Assert.That(quarter, Does.Not.Contain("ИТОГО"));
            Assert.That(quarter, Does.Contain("ВСЕГО"));
        });
    }

    [Test]
    public void TotalQuotaIsSumOfRoundedPerPersonQuotas()
    {
        var project = Project(("Dev1", 0, 0), ("Dev2", 0, 0));
        var threeDays = Start.AddDays(2);
        var quarter = QuarterDistributionExporter.Build(project, Start, threeDays);
        var daily = DailyPlanExporter.Build(project, Start, threeDays);

        Assert.Multiple(() =>
        {
            // По 3 дня на человека: округление 0,6 вверх даёт 1 + 1 = 2, а не round(6 × 0,2) = 1.
            Assert.That(QuarterLine(quarter, "ВСЕГО"), Does.Contain("│    6 │    2 /   2  ok"));
            Assert.That(daily, Does.Contain("Резерв: цель 2 дня, обеспечено 2."));
        });
    }

    [Test]
    public void CapacityStartsWhenDeveloperBecomesAvailable()
    {
        var project = Project(("Dev1", 0, 0));
        project = project with
        {
            Resources = [new ResourceDto
            {
                Name = "Dev1", Role = "DEV", AvailFrom = Start.AddDays(50)
            }]
        };

        var (quarter, daily) = Reports(project);

        Assert.Multiple(() =>
        {
            Assert.That(QuarterLine(quarter, "Dev1"), Does.Contain("│   50 │   10 /  10  ok"));
            Assert.That(DailySection(daily, "Dev1"),
                Does.Contain("Dev1, итог за период: рабочих дней 50 = 10 резерв + 40 ёмкость."));
        });
    }

    [Test]
    public void ReserveUsesWorkingDaysRatherThanCalendarDays()
    {
        var project = Project(("Dev1", 0, 0)) with { GlobalCalendar = null };
        var end = Start.AddDays(13);
        var quarter = QuarterDistributionExporter.Build(project, Start, end);
        var daily = DailyPlanExporter.Build(project, Start, end);

        Assert.Multiple(() =>
        {
            Assert.That(QuarterLine(quarter, "Dev1"), Does.Contain("│   10 │    2 /   2  ok"));
            Assert.That(daily, Does.Contain("Резерв: цель 2 дня, обеспечено 2."));
        });
    }

    [Test]
    public void CalendarQuarterEndsOnLastDayOfDecember()
    {
        var (start, end) = CalendarQuarter.Containing(new DateOnly(2026, 11, 12));

        Assert.Multiple(() =>
        {
            Assert.That(start, Is.EqualTo(new DateOnly(2026, 10, 1)));
            Assert.That(end, Is.EqualTo(new DateOnly(2026, 12, 31)));
        });
    }

    [Test]
    public void DailyAndQuarterReportsCanUseTheSameRoleFilter()
    {
        var project = Project(("Dev1", 30, 0), ("Dev2", 10, 0)) with
        {
            Resources =
            [
                new ResourceDto { Name = "Dev1", Role = "DEV" },
                new ResourceDto { Name = "Dev2", Role = "QA" }
            ]
        };
        string quarter;
        string daily;
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            quarter = QuarterDistributionExporter.Build(project, Start, End, roles: ["DEV"]);
            daily = DailyPlanExporter.Build(project, Start, End, roles: ["DEV"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Multiple(() =>
        {
            Assert.That(quarter, Does.Contain("Роли: DEV"));
            Assert.That(daily, Does.Contain("Роли: DEV"));
            Assert.That(quarter, Does.Not.Contain("Dev2"));
            Assert.That(daily, Does.Not.Contain("=== Dev2 ==="));
            Assert.That(QuarterLine(quarter, "ВСЕГО"), Does.Contain("│  100 │"));
            Assert.That(daily, Does.Contain("от всех 100 рабочих дней"));
        });
    }
}
