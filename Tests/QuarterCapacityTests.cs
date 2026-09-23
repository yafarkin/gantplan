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

    private static string QuarterLine(string report, string person) =>
        report.Split('\n').Single(line => line.TrimStart().StartsWith(person + " ", StringComparison.Ordinal));

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
            Assert.That(line, Does.Contain("доступно=100"));
            Assert.That(line, Does.Contain("влёт= 20"));
            Assert.That(line, Does.Contain("ёмкость= 80"));
            Assert.That(line, Does.Contain("бизнес= 30 ( 37,5%)"));
            Assert.That(line, Does.Contain("не-бизнес= 20 ( 25,0%)"));
            Assert.That(line, Does.Contain("простой= 30 ( 37,5%)"));
            Assert.That(QuarterLine(quarter, "Всего"), Does.Contain("простой= 30 ( 37,5%)"));
            Assert.That(daily, Does.Contain("бизнес 30 (37,5%)"));
            Assert.That(daily, Does.Contain("не-бизнес 20 (25,0%)"));
            Assert.That(daily, Does.Contain("не занят 30 (37,5%)"));
        });
    }

    [Test]
    public void FullyUsedReserveShowsOverloadForPersonAndTotal()
    {
        var (quarter, daily) = Reports(Project(("Dev1", 70, 30)));

        Assert.Multiple(() =>
        {
            Assert.That(QuarterLine(quarter, "Dev1"), Does.Contain("простой=  0 (  0,0%)  ПЕРЕГРУЗ +20"));
            Assert.That(QuarterLine(quarter, "Всего"), Does.Contain("ПЕРЕГРУЗ ПО ЛЮДЯМ +20"));
            Assert.That(DailySection(daily, "Dev1"), Does.Contain("ничем не занят: 0 (0,0%);  ПЕРЕГРУЗ +20"));
            Assert.That(daily, Does.Contain("не занят 0 (0,0%);  ПЕРЕГРУЗ ПО ЛЮДЯМ +20"));
        });
    }

    [Test]
    public void PersonOverloadRemainsVisibleWhenTeamHasFreeCapacity()
    {
        var (quarter, daily) = Reports(Project(("Dev1", 90, 0), ("Dev2", 0, 0)));

        Assert.Multiple(() =>
        {
            Assert.That(QuarterLine(quarter, "Dev1"), Does.Contain("ПЕРЕГРУЗ +10"));
            Assert.That(QuarterLine(quarter, "Dev2"), Does.Contain("простой= 80 (100,0%)"));
            Assert.That(QuarterLine(quarter, "Всего"), Does.Contain("простой= 80 ( 50,0%)  ПЕРЕГРУЗ ПО ЛЮДЯМ +10"));
            Assert.That(DailySection(daily, "Dev1"), Does.Contain("ПЕРЕГРУЗ +10"));
            Assert.That(DailySection(daily, "Dev2"), Does.Contain("ничем не занят: 80 (100,0%)"));
            Assert.That(daily, Does.Contain("не занят 80 (50,0%);  ПЕРЕГРУЗ ПО ЛЮДЯМ +10"));
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
            Assert.That(QuarterLine(quarter, "Всего"), Does.Contain("доступно=  6  влёт=  2  ёмкость=  4"));
            Assert.That(daily, Does.Contain("ИТОГО: рабочих дней 6"));
            Assert.That(daily, Does.Contain("влёты 2"));
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
            Assert.That(QuarterLine(quarter, "Dev1"),
                Does.Contain("доступно= 50  влёт= 10  ёмкость= 40"));
            Assert.That(DailySection(daily, "Dev1"), Does.Contain("Рабочих дней всего: 50"));
            Assert.That(daily, Does.Contain("влёты 10"));
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
            Assert.That(QuarterLine(quarter, "Dev1"),
                Does.Contain("доступно= 10  влёт=  2  ёмкость=  8"));
            Assert.That(daily, Does.Contain("ИТОГО: рабочих дней 10"));
            Assert.That(daily, Does.Contain("влёты 2"));
        });
    }
}
