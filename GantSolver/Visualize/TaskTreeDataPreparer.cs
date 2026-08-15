using System.Text;
using GantPlan.Dtos;
using Newtonsoft.Json;

namespace GantPlan.Visualize;

// Готовит данные для task_tree_template.html: в отличие от прежнего
// плоского "по одному ряду на исполнителя", сюда уходит дерево задач как
// есть, вместе с уже посчитанными датами (Solver.PostFillContainerTasks
// заполняет Plan и у контейнеров тоже - агрегатом по детям, так что
// пересчитывать даты для групп здесь не нужно).
public static class TaskTreeDataPreparer
{
    public static TreeContext Prepare(ProjectDto project)
    {
        var root = BuildNode(project, project.RootTask, colorGroupId: null, depth: 0);

        var globalNonWorking = ExpandPeriods(project.GlobalCalendar?.NonWorkingDays);
        var resourceNonWorking = project.Resources
            .Where(r => r.Calendar is not null)
            .ToDictionary(ResourceKey, r => ExpandPeriods(r.Calendar!.NonWorkingDays));

        return new TreeContext(root, globalNonWorking, resourceNonWorking);
    }

    /// <summary>
    ///     Генерирует итоговый HTML по шаблону.
    /// </summary>
    public static void GenerateHtml(string templatePath, string outputPath, TreeContext context)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Шаблон не найден", templatePath);
        }

        var template = File.ReadAllText(templatePath, Encoding.UTF8);

        var treeJson = JsonConvert.SerializeObject(
            context.Root,
            new JsonSerializerSettings {StringEscapeHandling = StringEscapeHandling.EscapeHtml}
        );
        var globalJson = JsonConvert.SerializeObject(context.GlobalNonWorking);
        var resourceJson = JsonConvert.SerializeObject(context.ResourceNonWorking);

        var output = template
            .Replace("{{TreeData}}", treeJson)
            .Replace("{{global_non_working}}", globalJson)
            .Replace("{{resource_non_working}}", resourceJson);

        File.WriteAllText(outputPath, output, Encoding.UTF8);
    }

    // Обходит дерево сверху вниз и строит по узлу на каждую задачу.
    // colorGroupId - id ближайшего предка на глубине 1 (прямого ребёнка
    // корня): всё поддерево под одним таким предком красится одним цветом,
    // независимо от того, как устроены id задач (раньше цвет угадывался по
    // первым двум сегментам dot-separated id, что ломалось при других
    // соглашениях об именовании).
    private static TaskNode BuildNode(ProjectDto project, TaskDto task, string? colorGroupId, int depth)
    {
        var effectiveGroupId = depth <= 1 ? task.Id : colorGroupId!;

        string? assignee = null;
        if (!task.HasChild && task.Plan?.ResourceName is { } resourceName)
        {
            var resource = project.Resources.SingleOrDefault(r => r.Name == resourceName);
            assignee = resource is not null ? ResourceKey(resource) : resourceName;
        }

        var jiraUrl = !string.IsNullOrWhiteSpace(task.JiraKey) && !string.IsNullOrWhiteSpace(project.BaseJiraUrl)
            ? project.BaseJiraUrl + task.JiraKey
            : null;

        var statusLabel = task switch
        {
            {Disabled: true} => "выключено",
            {Fact.IsPaused: true} => "на паузе",
            {Fact.IsFinished: true} => "завершено",
            _ => null
        };

        var children = task.Children
            .Select(child => BuildNode(project, child, effectiveGroupId, depth + 1))
            .ToList();

        return new TaskNode(
            task.Id,
            task.Name,
            task.JiraKey,
            jiraUrl,
            assignee,
            task.Plan?.PlannedStart,
            task.Plan?.PlannedFinish,
            statusLabel,
            effectiveGroupId,
            children);
    }

    private static string ResourceKey(ResourceDto resource) => $"{resource.Name}-{resource.Role}";

    private static DateOnly[] ExpandPeriods(ICollection<CalendarPeriod>? periods)
    {
        var result = new List<DateOnly>();
        if (periods is not null)
        {
            foreach (var period in periods)
            {
                var day = period.From;
                while (day <= period.To)
                {
                    result.Add(day);
                    day = day.AddDays(1);
                }
            }
        }

        return result.ToArray();
    }

    public sealed record TaskNode(
        string Id,
        string Name,
        string? JiraKey,
        string? JiraUrl,
        string? Assignee,
        DateOnly? Start,
        DateOnly? End,
        string? StatusLabel,
        string ColorGroupId,
        List<TaskNode> Children
    );

    public sealed record TreeContext(
        TaskNode Root,
        DateOnly[] GlobalNonWorking,
        Dictionary<string, DateOnly[]> ResourceNonWorking
    );
}
