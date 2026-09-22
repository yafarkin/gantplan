using System.Text;
using GantPlan.Dtos;
using GantPlan.Dtos.Enums;

namespace GantPlan.Visualize;

// После расчёта строит текстовый файл с планом по людям и дням: для каждого
// сотрудника на каждый день от начала плана до конца выводится, какой задачей
// он занят (и каким по счёту рабочим днём этой задачи), либо что день
// нерабочий либо задач не назначено.
public static class DailyPlanExporter
{
    public static string Build(
        ProjectDto project,
        DateOnly? quarterStart = null,
        DateOnly? quarterEnd = null,
        double vletPercent = 0.20)
    {
        // Окно квартала определяется от даты плана, если не передано явно.
        var qs = quarterStart ?? StartOfQuarter(project.ProjectStart);
        var qe = quarterEnd ?? qs.AddMonths(3).AddDays(-1);

        var leaves = new List<TaskDto>();
        CollectLeaves(project.RootTask, leaves);

        var tasksByResource = leaves
            .Where(t => t.Plan?.PlannedStart is not null
                        && t.Plan.PlannedFinish is not null
                        && !string.IsNullOrWhiteSpace(t.Plan.ResourceName))
            .GroupBy(t => t.Plan!.ResourceName!)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Plan!.PlannedStart).ToList());

        var globalNonWorking = WorkingDayCalculator.ExpandPeriods(project.GlobalCalendar?.NonWorkingDays);
        var globalWorking = WorkingDayCalculator.ExpandPeriods(project.GlobalCalendar?.WorkingDays);

        var endDate = project.ProjectStart;
        foreach (var tasks in tasksByResource.Values)
        {
            foreach (var task in tasks)
            {
                if (task.Plan!.PlannedFinish!.Value > endDate)
                {
                    endDate = task.Plan!.PlannedFinish!.Value;
                }
            }
        }

        var body = new StringBuilder();
        var totalAvail = 0;
        var totalBiz = 0;
        var totalNonBiz = 0;
        var totalVlet = 0;
        var totalIdle = 0;

        foreach (var resource in project.Resources)
        {
            if (!tasksByResource.TryGetValue(resource.Name, out var tasks))
            {
                continue;
            }

            var resourceNonWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.NonWorkingDays);
            var resourceWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.WorkingDays);

            body.AppendLine($"=== {resource.Name} ===");
            for (var day = project.ProjectStart; day <= endDate; day = day.AddDays(1))
            {
                var dateStr = FormatDate(day);
                if (!WorkingDayCalculator.IsWorkingDay(day, globalNonWorking, globalWorking, resourceNonWorking, resourceWorking))
                {
                    body.AppendLine($"{resource.Name}: {dateStr}; не рабочий день");
                    continue;
                }

                var task = tasks.FirstOrDefault(t => day >= t.Plan!.PlannedStart && day <= t.Plan!.PlannedFinish);
                if (task is null)
                {
                    body.AppendLine($"{resource.Name}: {dateStr}; нет задач");
                    continue;
                }

                var total = WorkingDayCalculator.CountWorkingDays(
                    task.Plan!.PlannedStart!.Value, task.Plan!.PlannedFinish!.Value,
                    globalNonWorking, globalWorking, resourceNonWorking, resourceWorking);
                var index = WorkingDayCalculator.CountWorkingDays(
                    task.Plan!.PlannedStart!.Value, day,
                    globalNonWorking, globalWorking, resourceNonWorking, resourceWorking);

                body.AppendLine(FormatTaskLine(resource.Name, dateStr, task, index, total));
            }

            var (qAvail, qBiz, qNonBiz) = CountQuarter(
                tasks, qs, qe, globalNonWorking, globalWorking, resourceNonWorking, resourceWorking);
            var vlet = Round(vletPercent * qAvail);
            var capacity = qAvail - vlet;
            var qIdle = Math.Max(0, capacity - qBiz - qNonBiz);

            totalAvail += qAvail;
            totalBiz += qBiz;
            totalNonBiz += qNonBiz;
            totalVlet += vlet;
            totalIdle += qIdle;

            body.AppendLine($"Рабочих дней всего: {qAvail};  из них на бизнес: {qBiz} ({Pct(qBiz, capacity)})");
            body.AppendLine($"не на бизнес: {qNonBiz} ({Pct(qNonBiz, capacity)});  " +
                            $"на влёты осталось: {vlet} ({vletPercent:P0});  " +
                            $"ничем не занят: {qIdle} ({Pct(qIdle, capacity)})");
            body.AppendLine();
        }

        var totalCapacity = totalAvail - totalVlet;
        var totalOverload = Math.Max(0, totalBiz + totalNonBiz - totalCapacity);

        var sb = new StringBuilder();
        sb.AppendLine($"===== Квартал {FormatDateShort(qs)} – {FormatDateShort(qe)}, квота «влёт» {vletPercent:P0} =====");
        sb.AppendLine($"ИТОГО: рабочих дней {totalAvail};  бизнес {totalBiz} ({Pct(totalBiz, totalCapacity)});  " +
                      $"не-бизнес {totalNonBiz} ({Pct(totalNonBiz, totalCapacity)});  " +
                      $"влёты {totalVlet} ({vletPercent:P0});  не занят {totalIdle} ({Pct(totalIdle, totalCapacity)})" +
                      (totalOverload > 0 ? $";  ПЕРЕГРУЗ +{totalOverload}" : string.Empty));
        sb.AppendLine();
        sb.Append(body.ToString());

        return sb.ToString();
    }

    private static void CollectLeaves(TaskDto task, List<TaskDto> leaves)
    {
        if (task.HasChild)
        {
            foreach (var child in task.Children)
            {
                CollectLeaves(child, leaves);
            }
        }
        else
        {
            leaves.Add(task);
        }
    }

    private static string FormatTaskLine(string resourceName, string dateStr, TaskDto task, int index, int total)
    {
        var namePart = string.IsNullOrWhiteSpace(task.JiraKey)
            ? $"[{task.Name}]"
            : $"{task.JiraKey} [{task.Name}]";

        var size = task.Limit?.TShirt is { } tShirt && tShirt != TShirtType.None
            ? $" ({tShirt})"
            : string.Empty;

        return $"{resourceName}: {dateStr}; {namePart}{size}, {index}/{total}";
    }

    private static bool IsBusiness(TaskDto task)
    {
        var workTypeStr = task.Tags?.GetValueOrDefault(TaskTagKeys.WorkType);
        return Enum.TryParse<WorkType>(workTypeStr, out var workType) && workType == WorkType.Business;
    }

    // Считает по окну квартала: доступные рабочие дни и сколько из них ушло на
    // бизнес/не-бизнес задачи (дни без задачи в счётчики не попадают).
    private static (int avail, int biz, int nonBiz) CountQuarter(
        ICollection<TaskDto> tasks, DateOnly from, DateOnly to,
        DateOnly[] globalNonWorking, DateOnly[] globalWorking,
        DateOnly[] resourceNonWorking, DateOnly[] resourceWorking)
    {
        var avail = 0;
        var biz = 0;
        var nonBiz = 0;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (!WorkingDayCalculator.IsWorkingDay(day, globalNonWorking, globalWorking, resourceNonWorking, resourceWorking))
            {
                continue;
            }

            avail++;
            var task = tasks.FirstOrDefault(t => day >= t.Plan!.PlannedStart && day <= t.Plan!.PlannedFinish);
            if (task is null)
            {
                continue;
            }

            if (IsBusiness(task))
            {
                biz++;
            }
            else
            {
                nonBiz++;
            }
        }

        return (avail, biz, nonBiz);
    }

    private static string Pct(int value, int total) =>
        total > 0 ? $"{100.0 * value / total:0.0}%" : "0.0%";

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static DateOnly StartOfQuarter(DateOnly date)
    {
        var startMonth = ((date.Month - 1) / 3) * 3 + 1;
        return new DateOnly(date.Year, startMonth, 1);
    }

    private static string FormatDate(DateOnly day) => $"{day:dd.MM.yyyy}, {DayAbbreviation(day.DayOfWeek)}.";

    private static string FormatDateShort(DateOnly day) => $"{day:dd.MM.yyyy}";

    private static string DayAbbreviation(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "пн",
        DayOfWeek.Tuesday => "вт",
        DayOfWeek.Wednesday => "ср",
        DayOfWeek.Thursday => "чт",
        DayOfWeek.Friday => "пт",
        DayOfWeek.Saturday => "сб",
        DayOfWeek.Sunday => "вс",
        _ => string.Empty
    };
}