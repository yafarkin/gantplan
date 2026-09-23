using System.Text;
using GantPlan.Dtos;
using GantPlan.Dtos.Enums;

namespace GantPlan.Visualize;

// Статистика распределения рабочего времени по кварталу с учётом виртуальной
// квоты «влёт» (внеплановая работа, по умолчанию 20% от доступных рабочих
// дней). Квота вычитается из ёмкости: проценты бизнес/не-бизнес считаются от
// оставшихся 80%, а перегруз виден, когда план заполняет больше ёмкости.
public static class QuarterDistributionExporter
{
    public static string Build(
        ProjectDto project,
        DateOnly quarterStart,
        DateOnly quarterEnd,
        double unplannedWorkReserveShare = 0.20,
        IReadOnlyCollection<string>? roles = null)
    {
        QuarterCapacity.ValidateReserveShare(unplannedWorkReserveShare);

        var leaves = new List<TaskDto>();
        CollectLeaves(project.RootTask, leaves);

        var tasksByResource = leaves
            .Where(t => t.Plan?.PlannedStart is not null
                        && t.Plan.PlannedFinish is not null
                        && !string.IsNullOrWhiteSpace(t.Plan.ResourceName))
            .GroupBy(t => t.Plan!.ResourceName!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var globalNonWorking = WorkingDayCalculator.ExpandPeriods(project.GlobalCalendar?.NonWorkingDays);
        var globalWorking = WorkingDayCalculator.ExpandPeriods(project.GlobalCalendar?.WorkingDays);

        var resources = project.Resources
            .Where(r => roles is null || roles.Contains(r.Role))
            .OrderBy(r => r.Name)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"===== Распределение за период {FormatDate(quarterStart)} – {FormatDate(quarterEnd)} =====");
        sb.AppendLine(roles is null ? "Роли: все" : $"Роли: {string.Join(", ", roles)}");
        sb.AppendLine($"Целевой резерв на внеплановые работы: {unplannedWorkReserveShare:P0} от рабочих дней");
        sb.AppendLine("Доли бизнеса и остальных задач ниже считаются от ёмкости после резерва.");
        sb.AppendLine("При дефиците резерва сумма долей задач превышает 100% на величину дефицита.");
        sb.AppendLine();

        var tAvail = 0;
        var tBiz = 0;
        var tNonBiz = 0;
        var tIdle = 0;
        var tQuota = 0;
        var tOverload = 0;

        foreach (var resource in resources)
        {
            var (avail, biz, nonBiz) = Count(resource, quarterStart, quarterEnd,
                tasksByResource.GetValueOrDefault(resource.Name) ?? [], globalNonWorking, globalWorking);

            var allocation = QuarterCapacity.Calculate(avail, biz, nonBiz, unplannedWorkReserveShare);

            tAvail += avail;
            tBiz += biz;
            tNonBiz += nonBiz;
            tIdle += allocation.Idle;
            tQuota += allocation.Quota;
            tOverload += allocation.Overload;

            AppendResourceLine(sb, resource.Name, avail, allocation.Quota, allocation.Capacity,
                biz, nonBiz, allocation.Idle, allocation.Overload);
        }

        sb.AppendLine();
        sb.AppendLine("ИТОГО:");
        var tCapacity = tAvail - tQuota;
        AppendResourceLine(sb, "Всего", tAvail, tQuota, tCapacity, tBiz, tNonBiz, tIdle, tOverload,
            total: true);
        sb.AppendLine($"От всех рабочих дней: бизнес {Percent(tBiz, tAvail):0.0}%, " +
                      $"остальные задачи {Percent(tNonBiz, tAvail):0.0}%, " +
                      $"без задач {Percent(tAvail - tBiz - tNonBiz, tAvail):0.0}%.");

        return sb.ToString();
    }

    private static (int avail, int biz, int nonBiz) Count(
        ResourceDto resource,
        DateOnly quarterStart, DateOnly quarterEnd,
        ICollection<TaskDto> tasks,
        DateOnly[] globalNonWorking, DateOnly[] globalWorking)
    {
        var resourceNonWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.NonWorkingDays);
        var resourceWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.WorkingDays);

        var avail = 0;
        var biz = 0;
        var nonBiz = 0;
        for (var day = quarterStart; day <= quarterEnd; day = day.AddDays(1))
        {
            if ((resource.AvailFrom is { } from && day < from)
                || (resource.AvailTo is { } to && day > to)
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

    private static bool IsBusiness(TaskDto task)
    {
        var workTypeStr = task.Tags?.GetValueOrDefault(TaskTagKeys.WorkType);
        return Enum.TryParse<WorkType>(workTypeStr, out var workType) && workType == WorkType.Business;
    }

    private static void AppendResourceLine(
        StringBuilder sb, string name, int avail, int quota, int capacity,
        int biz, int nonBiz, int idle, int overload, bool total = false)
    {
        var pctBiz = capacity > 0 ? 100.0 * biz / capacity : 0;
        var pctNonBiz = capacity > 0 ? 100.0 * nonBiz / capacity : 0;
        var unassigned = avail - biz - nonBiz;
        var coveredReserve = total ? quota - overload : Math.Min(quota, unassigned);
        var overloadSuffix = overload > 0
            ? $"  {(total ? "ДЕФИЦИТ РЕЗЕРВА ПО ЛЮДЯМ" : "ДЕФИЦИТ РЕЗЕРВА")} " +
              $"+{overload} ({Percent(overload, capacity):0.0}% ёмкости)"
            : string.Empty;

        sb.AppendLine(
            $"{name,-10} доступно={avail,3}  резерв: цель={quota,3}, обеспечено={coveredReserve,3}  " +
            $"ёмкость={capacity,3}  " +
            $"бизнес={biz,3} ({pctBiz,5:0.0}%)  не-бизнес={nonBiz,3} ({pctNonBiz,5:0.0}%)  " +
            $"без задач={unassigned,3}  свободно сверх резерва={idle,3} " +
            $"({Percent(idle, capacity),5:0.0}%){overloadSuffix}");
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

    private static string FormatDate(DateOnly day) => $"{day:dd.MM.yyyy}";

    private static double Percent(int value, int total) => total > 0 ? 100.0 * value / total : 0;
}
