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
        double unplannedWorkReserveShare = 0.20,
        IReadOnlyCollection<string>? roles = null)
    {
        QuarterCapacity.ValidateReserveShare(unplannedWorkReserveShare);

        var (defaultStart, defaultEnd) = CalendarQuarter.Containing(project.ProjectStart);
        var qs = quarterStart ?? defaultStart;
        var qe = quarterEnd ?? (quarterStart.HasValue ? CalendarQuarter.Containing(qs).End : defaultEnd);

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

        var resources = project.Resources.Where(r => roles is null || roles.Contains(r.Role)).ToList();

        var endDate = project.ProjectStart;
        foreach (var resource in resources)
        {
            if (!tasksByResource.TryGetValue(resource.Name, out var tasks)) continue;
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
        var totalReserve = 0;
        var totalIdle = 0;
        var totalOverload = 0;

        foreach (var resource in resources)
        {
            var tasks = tasksByResource.GetValueOrDefault(resource.Name) ?? [];

            var resourceNonWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.NonWorkingDays);
            var resourceWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.WorkingDays);

            body.AppendLine($"=== {resource.Name} ===");
            for (var day = project.ProjectStart; day <= endDate; day = day.AddDays(1))
            {
                var dateStr = FormatDate(day);
                if ((resource.AvailFrom is { } from && day < from)
                    || (resource.AvailTo is { } to && day > to))
                {
                    body.AppendLine($"{resource.Name}: {dateStr}; недоступен");
                    continue;
                }

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
                tasks, resource, qs, qe, globalNonWorking, globalWorking, resourceNonWorking, resourceWorking);
            var allocation = QuarterCapacity.Calculate(qAvail, qBiz, qNonBiz, unplannedWorkReserveShare);

            totalAvail += qAvail;
            totalBiz += qBiz;
            totalNonBiz += qNonBiz;
            totalReserve += allocation.Quota;
            totalIdle += allocation.Idle;
            totalOverload += allocation.Overload;

            var unassigned = qAvail - qBiz - qNonBiz;
            var coveredReserve = Math.Min(allocation.Quota, unassigned);
            body.AppendLine($"Рабочих дней всего: {qAvail};  из них на бизнес: {qBiz} ({Pct(qBiz, allocation.Capacity)})");
            body.AppendLine($"не на бизнес: {qNonBiz} ({Pct(qNonBiz, allocation.Capacity)});  " +
                            $"без задач: {unassigned};  " +
                            $"резерв: цель {allocation.Quota} ({unplannedWorkReserveShare:P0}), обеспечено {coveredReserve};  " +
                            $"свободно сверх резерва: {allocation.Idle} ({Pct(allocation.Idle, allocation.Capacity)})" +
                            (allocation.Overload > 0
                                ? $";  ДЕФИЦИТ РЕЗЕРВА +{allocation.Overload} " +
                                  $"({Pct(allocation.Overload, allocation.Capacity)} ёмкости)"
                                : string.Empty));
            body.AppendLine();
        }

        var totalCapacity = totalAvail - totalReserve;
        var totalUnassigned = totalAvail - totalBiz - totalNonBiz;
        var totalCoveredReserve = totalReserve - totalOverload;

        var sb = new StringBuilder();
        sb.AppendLine($"===== Сводка за период {FormatDateShort(qs)} – {FormatDateShort(qe)}, " +
                      $"резерв {unplannedWorkReserveShare:P0} =====");
        sb.AppendLine(roles is null ? "Роли: все" : $"Роли: {string.Join(", ", roles)}");
        sb.AppendLine("Доли бизнеса и остальных задач считаются от ёмкости после резерва.");
        sb.AppendLine("При дефиците резерва сумма долей задач превышает 100% на величину дефицита.");
        sb.AppendLine($"ИТОГО: рабочих дней {totalAvail};  бизнес {totalBiz} ({Pct(totalBiz, totalCapacity)});  " +
                      $"не-бизнес {totalNonBiz} ({Pct(totalNonBiz, totalCapacity)});  " +
                      $"без задач {totalUnassigned};  " +
                      $"резерв: цель {totalReserve} ({unplannedWorkReserveShare:P0}), обеспечено {totalCoveredReserve};  " +
                      $"свободно сверх резерва {totalIdle} ({Pct(totalIdle, totalCapacity)})" +
                      (totalOverload > 0
                          ? $";  ДЕФИЦИТ РЕЗЕРВА ПО ЛЮДЯМ +{totalOverload} " +
                            $"({Pct(totalOverload, totalCapacity)} ёмкости)"
                          : string.Empty));
        sb.AppendLine($"От всех рабочих дней: бизнес {Pct(totalBiz, totalAvail)}, " +
                      $"остальные задачи {Pct(totalNonBiz, totalAvail)}, " +
                      $"без задач {Pct(totalUnassigned, totalAvail)}.");
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
        ICollection<TaskDto> tasks, ResourceDto resource, DateOnly from, DateOnly to,
        DateOnly[] globalNonWorking, DateOnly[] globalWorking,
        DateOnly[] resourceNonWorking, DateOnly[] resourceWorking)
    {
        var avail = 0;
        var biz = 0;
        var nonBiz = 0;

        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if ((resource.AvailFrom is { } availableFrom && day < availableFrom)
                || (resource.AvailTo is { } availableTo && day > availableTo)
                || !WorkingDayCalculator.IsWorkingDay(day, globalNonWorking, globalWorking, resourceNonWorking, resourceWorking))
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
