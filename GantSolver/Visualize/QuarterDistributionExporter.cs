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
        double vletPercent = 0.20,
        IReadOnlyCollection<string>? roles = null)
    {
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
            .Where(r => (roles is null || roles.Contains(r.Role)) && tasksByResource.ContainsKey(r.Name))
            .OrderBy(r => r.Name)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"===== Распределение по кварталу {FormatDate(quarterStart)} – {FormatDate(quarterEnd)} =====");
        sb.AppendLine(roles is null ? "Роли: все" : $"Роли: {string.Join(", ", roles)}");
        sb.AppendLine($"Квота «влёт»: {vletPercent:P0} (вычитается из ёмкости)");
        sb.AppendLine();

        var tAvail = 0;
        var tBiz = 0;
        var tNonBiz = 0;
        var tIdle = 0;

        foreach (var resource in resources)
        {
            var (avail, biz, nonBiz, idle) = Count(project, resource, quarterStart, quarterEnd,
                tasksByResource[resource.Name], globalNonWorking, globalWorking);

            var quota = Round(vletPercent * avail);
            var capacity = avail - quota;
            var overload = Math.Max(0, biz + nonBiz - capacity);

            tAvail += avail;
            tBiz += biz;
            tNonBiz += nonBiz;
            tIdle += idle;

            AppendResourceLine(sb, resource.Name, avail, quota, capacity, biz, nonBiz, idle, overload);
        }

        sb.AppendLine();
        sb.AppendLine("ИТОГО:");
        var tQuota = Round(vletPercent * tAvail);
        var tCapacity = tAvail - tQuota;
        var tOverload = Math.Max(0, tBiz + tNonBiz - tCapacity);
        AppendResourceLine(sb, "Всего", tAvail, tQuota, tCapacity, tBiz, tNonBiz, tIdle, tOverload);

        return sb.ToString();
    }

    private static (int avail, int biz, int nonBiz, int idle) Count(
        ProjectDto project, ResourceDto resource,
        DateOnly quarterStart, DateOnly quarterEnd,
        ICollection<TaskDto> tasks,
        DateOnly[] globalNonWorking, DateOnly[] globalWorking)
    {
        var resourceNonWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.NonWorkingDays);
        var resourceWorking = WorkingDayCalculator.ExpandPeriods(resource.Calendar?.WorkingDays);

        var avail = 0;
        var biz = 0;
        var nonBiz = 0;
        var idle = 0;

        for (var day = quarterStart; day <= quarterEnd; day = day.AddDays(1))
        {
            if (!WorkingDayCalculator.IsWorkingDay(day, globalNonWorking, globalWorking, resourceNonWorking, resourceWorking))
            {
                continue;
            }

            avail++;
            var task = tasks.FirstOrDefault(t => day >= t.Plan!.PlannedStart && day <= t.Plan!.PlannedFinish);
            if (task is null)
            {
                idle++;
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

        return (avail, biz, nonBiz, idle);
    }

    private static bool IsBusiness(TaskDto task)
    {
        var workTypeStr = task.Tags?.GetValueOrDefault(TaskTagKeys.WorkType);
        return Enum.TryParse<WorkType>(workTypeStr, out var workType) && workType == WorkType.Business;
    }

    private static void AppendResourceLine(
        StringBuilder sb, string name, int avail, int quota, int capacity,
        int biz, int nonBiz, int idle, int overload)
    {
        var pctBiz = capacity > 0 ? 100.0 * biz / capacity : 0;
        var pctNonBiz = capacity > 0 ? 100.0 * nonBiz / capacity : 0;
        var overloadSuffix = overload > 0 ? $"  ПЕРЕГРУЗ +{overload}" : string.Empty;

        sb.AppendLine(
            $"{name,-10} доступно={avail,3}  влёт={quota,3}  ёмкость={capacity,3}  " +
            $"бизнес={biz,3} ({pctBiz,5:0.0}%)  не-бизнес={nonBiz,3} ({pctNonBiz,5:0.0}%)  " +
            $"простой={idle,3}{overloadSuffix}");
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

    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static string FormatDate(DateOnly day) => $"{day:dd.MM.yyyy}";
}