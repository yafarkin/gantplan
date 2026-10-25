using GantPlan.Dtos;
using GantPlan.Visualize;

namespace Tests;

public class TaskTreeDataPreparerTests
{
    // 10.01.2026 (сб) - общая рабочая суббота, у doe личный рабочий день в
    // сб 17.01, у john отпуск 14-15.01. Задача на всё время 05.01-21.01,
    // чтобы календарь покрыл весь диапазон.
    private static ProjectDto CalendarProject() => new()
    {
        ProjectStart = new DateOnly(2026, 1, 5),
        GlobalCalendar = new CalendarDto
        {
            WorkingDays = [new CalendarPeriod { From = new DateOnly(2026, 1, 10), To = new DateOnly(2026, 1, 10) }]
        },
        RootTask = new TaskDto
        {
            Id = "1",
            Name = "{{fact_date}} </script>",
            Plan = new TaskPlanDto
            {
                ResourceName = "john",
                PlannedStart = new DateOnly(2026, 1, 5),
                PlannedFinish = new DateOnly(2026, 1, 21)
            }
        },
        Resources =
        [
            new ResourceDto
            {
                Role = "dev", Name = "john",
                Calendar = new CalendarDto
                {
                    NonWorkingDays = [new CalendarPeriod { From = new DateOnly(2026, 1, 14), To = new DateOnly(2026, 1, 15) }]
                }
            },
            new ResourceDto
            {
                Role = "dev", Name = "doe",
                Calendar = new CalendarDto
                {
                    WorkingDays = [new CalendarPeriod { From = new DateOnly(2026, 1, 17), To = new DateOnly(2026, 1, 17) }]
                }
            }
        ]
    };

    // Нерабочие дни для таймлайна считаются тем же CalendarLogic, что и у
    // солвера: рабочие переносы и личные рабочие дни не штрихуются, хотя
    // это суббота.
    [Test]
    public void NonWorkingDaysShouldFollowSolverCalendarTest()
    {
        var context = TaskTreeDataPreparer.Prepare(CalendarProject());

        Assert.That(context.GlobalNonWorking, Does.Not.Contain(new DateOnly(2026, 1, 10)));
        Assert.That(context.GlobalNonWorking, Does.Contain(new DateOnly(2026, 1, 11)));
        Assert.That(context.GlobalNonWorking, Does.Contain(new DateOnly(2026, 1, 17)));

        Assert.That(context.ResourceNonWorking["doe-dev"], Does.Not.Contain(new DateOnly(2026, 1, 17)));
        Assert.That(context.ResourceNonWorking["doe-dev"], Does.Contain(new DateOnly(2026, 1, 18)));

        Assert.That(context.ResourceNonWorking["john-dev"], Does.Contain(new DateOnly(2026, 1, 14)));
        Assert.That(context.ResourceNonWorking["john-dev"], Does.Contain(new DateOnly(2026, 1, 17)));
        Assert.That(context.ResourceNonWorking["john-dev"], Does.Not.Contain(new DateOnly(2026, 1, 16)));
    }

    // Границы доступности попадают в данные таймлайна только у тех, у кого
    // они заданы, - их шаблон рисует линией на границе, а не штриховкой.
    [Test]
    public void AvailabilityShouldBePassedOnlyForResourcesWithBoundsTest()
    {
        var project = CalendarProject();
        project = project with
        {
            Resources = project.Resources
                .Select(r => r.Name == "john" ? r with { AvailFrom = new DateOnly(2026, 1, 7), AvailTo = new DateOnly(2026, 1, 20) } : r)
                .ToList()
        };

        var context = TaskTreeDataPreparer.Prepare(project);

        Assert.That(context.ResourceAvailability.Keys, Is.EquivalentTo(new[] {"john-dev"}));
        Assert.That(context.ResourceAvailability["john-dev"],
            Is.EqualTo(new TaskTreeDataPreparer.Availability(new DateOnly(2026, 1, 7), new DateOnly(2026, 1, 20))));
    }

    // d3 встраивается в HTML (страница работает без интернета), а данные
    // подставляются одним проходом: "{{fact_date}}" в названии задачи
    // остаётся текстом, а "</script>" экранируется.
    [Test]
    public void GeneratedHtmlShouldInlineD3AndKeepDataAsIsTest()
    {
        var template = Path.Combine(TestContext.CurrentContext.TestDirectory, "Templates", "task_tree_template.html");
        var output = Path.Combine(Path.GetTempPath(), $"timeline-{Guid.NewGuid():N}.html");

        try
        {
            TaskTreeDataPreparer.GenerateHtml(template, output, TaskTreeDataPreparer.Prepare(CalendarProject()));
            var html = File.ReadAllText(output);

            Assert.That(html, Does.Contain("https://d3js.org v7.9.0"));
            Assert.That(html, Does.Not.Contain("<script src="));
            Assert.That(html, Does.Not.Contain("{{d3_script}}"));
            Assert.That(html, Does.Contain("\"Name\":\"{{fact_date}} \\u003c/script\\u003e\""));
        }
        finally
        {
            File.Delete(output);
        }
    }

    // Задача, ждущая подтверждения закрытия: в данных для таймлайна - флаг
    // (по нему бар выделяется даже в свёрнутом треке) и метка статуса, но
    // не приглушение - это задача, требующая внимания, а не завершённая.
    [Test]
    public void AwaitingClosureTaskShouldBeFlaggedNotMutedTest()
    {
        var project = CalendarProject();
        project.RootTask.Plan!.AwaitingClosure = true;

        var root = TaskTreeDataPreparer.Prepare(project).Root;

        Assert.That(root.AwaitingClosure, Is.True);
        Assert.That(root.StatusLabel, Is.EqualTo("ждёт подтверждения закрытия"));
        Assert.That(root.Muted, Is.False);
    }
}
