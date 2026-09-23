using System.Globalization;
using GantPlan.Dtos;
using GantPlan.Visualize;

namespace Tests;

// Сценарий: 1 человек, окно в 100 календарных дней (01.10.2026 – 08.01.2027),
// все дни рабочие (GlobalCalendar.WorkingDays). Квота «влёт» по умолчанию 20%.
public sealed class DailyPlanExporterTests
{
    private const string Person = "Человек";

    private static readonly DateOnly Start = new(2026, 10, 1);
    private static readonly DateOnly End = new(2027, 1, 8);

    private static TaskDto Task(string id, string name, string workType, DateOnly from, DateOnly to) => new()
    {
        Id = id,
        Name = name,
        Tags = new Dictionary<string, string> { [TaskTagKeys.WorkType] = workType },
        Plan = new TaskPlanDto { ResourceName = Person, PlannedStart = from, PlannedFinish = to }
    };

    private static ProjectDto BuildProject(int bizDays, int techDays)
    {
        var tasks = new List<TaskDto>();
        var cursor = Start;
        if (bizDays > 0)
        {
            tasks.Add(Task("biz", "Бизнес", "Business", cursor, cursor.AddDays(bizDays - 1)));
            cursor = cursor.AddDays(bizDays);
        }
        if (techDays > 0)
        {
            tasks.Add(Task("tech", "Техника", "Team", cursor, cursor.AddDays(techDays - 1)));
        }

        return new ProjectDto
        {
            ProjectStart = Start,
            GlobalCalendar = new CalendarDto
            {
                WorkingDays = new[] { new CalendarPeriod { From = Start, To = End } }
            },
            Resources = new[] { new ResourceDto { Name = Person, Role = "DEV", Percent = 100 } },
            RootTask = new TaskDto { Id = "root", Name = "root", Children = tasks }
        };
    }

    // ru-RU, чтобы разделитель десятичной дроби в процентах был детерминированным.
    private static string Build(ProjectDto project, double unplannedWorkReserveShare = 0.20)
    {
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            return DailyPlanExporter.Build(project, Start, End, unplannedWorkReserveShare);
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Test]
    public void Business15_NonBusiness10_With20Quota_Gives15_10_20_55()
    {
        var report = Build(BuildProject(bizDays: 15, techDays: 10));

        Assert.That(report, Does.Contain($"{Person}, итог за период: рабочих дней 100 = 20 резерв + 80 ёмкость."));
        Assert.That(report, Does.Contain("бизнес 15"));
        Assert.That(report, Does.Contain("не-бизнес 10"));
        Assert.That(report, Does.Contain("свободно сверх резерва 55"));
        Assert.That(report, Does.Contain("Резерв: цель 20 дней, обеспечено 20."));
    }

    [Test]
    public void Percentages_AreRelativeToCapacity_AfterVletDeduction()
    {
        var report = Build(BuildProject(bizDays: 15, techDays: 10));

        // Ёмкость = 100 − 20 (влёты) = 80: 15/80=19%, 10/80=13%, 55/80=69%.
        Assert.That(report, Does.Contain("бизнес 15 (19% ёмкости);  не-бизнес 10 (13%);  свободно сверх резерва 55 (69%)"));
        Assert.That(report, Does.Contain("Итого: бизнес 15 из 80 дней ёмкости (19%), это 15% от всех 100 рабочих дней."));
    }

    [Test]
    public void Overload_WhenPlanExceedsCapacityAfterVlet()
    {
        // Бизнес 70 + техника 30 = 100 > ёмкость 80 → перегруз +20, свободного нет.
        var report = Build(BuildProject(bizDays: 70, techDays: 30));

        Assert.That(report, Does.Contain("резерв обеспечен: 0 из 20;  НЕ ХВАТАЕТ 20 дн."));
        Assert.That(report, Does.Contain("Задачами занято 100 дней при ёмкости 80, перебор 20 дней."));
        Assert.That(report, Does.Contain("свободно сверх резерва 0"));
    }

    [Test]
    public void ZeroQuota_MeansNoVletDeduction()
    {
        var report = Build(BuildProject(bizDays: 15, techDays: 10), unplannedWorkReserveShare: 0.0);

        // Без влётов ёмкость = 100: бизнес 15/100, техника 10/100, свободно 75/100.
        Assert.That(report, Does.Contain("рабочих дней 100 = 0 резерв + 100 ёмкость."));
        Assert.That(report, Does.Contain("бизнес 15 (15% ёмкости)"));
        Assert.That(report, Does.Contain("свободно сверх резерва 75 (75%)"));
    }

    [Test]
    public void QuarterWindow_IsDerivedFromProjectStart_WhenNotPassed()
    {
        // ProjectStart 01.10.2026 → квартал 01.10–31.12 (календарный), окно покрывает все дни.
        var prev = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
        try
        {
            var project = BuildProject(bizDays: 15, techDays: 10);
            var report = DailyPlanExporter.Build(project);
            Assert.That(report, Does.Contain("Сводка за период 01.10.2026 – 31.12.2026"));
        }
        finally
        {
            CultureInfo.CurrentCulture = prev;
        }
    }

    [Test]
    public void ExplicitStartWithoutEnd_StopsAtEndOfCalendarQuarter()
    {
        var project = BuildProject(bizDays: 15, techDays: 10);
        var report = DailyPlanExporter.Build(project, quarterStart: new DateOnly(2026, 11, 12));

        Assert.That(report, Does.Contain("Сводка за период 12.11.2026 – 31.12.2026"));
    }
}
