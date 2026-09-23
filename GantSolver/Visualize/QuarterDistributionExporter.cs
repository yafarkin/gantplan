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

        var rows = project.Resources
            .Where(r => roles is null || roles.Contains(r.Role))
            .OrderBy(r => r.Name)
            .Select(r =>
            {
                var (avail, biz, nonBiz) = Count(r, quarterStart, quarterEnd,
                    tasksByResource.GetValueOrDefault(r.Name) ?? [], globalNonWorking, globalWorking);
                return new CapacityRow(r.Name, CapacityReport.NormalizeRole(r.Role), avail, biz, nonBiz,
                    QuarterCapacity.Calculate(avail, biz, nonBiz, unplannedWorkReserveShare));
            })
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"===== Распределение за период {FormatDate(quarterStart)} – {FormatDate(quarterEnd)} =====");
        sb.AppendLine(roles is null ? "Роли: все" : $"Роли: {string.Join(", ", roles)}");
        sb.Append(CapacityReport.Render(rows, unplannedWorkReserveShare));

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
}
