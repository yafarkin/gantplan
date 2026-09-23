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
        var colors = TaskColorPalette.Build(project.RootTask);
        var root = BuildNode(project, project.RootTask, colors, epicJiraKey: null, epicJiraUrl: null);

        var globalNonWorking = ExpandPeriods(project.GlobalCalendar?.NonWorkingDays);
        var resourceNonWorking = project.Resources
            .Where(r => r.Calendar is not null)
            .ToDictionary(ResourceKey, r => ExpandPeriods(r.Calendar!.NonWorkingDays));

        return new TreeContext(root, globalNonWorking, resourceNonWorking, project.FactDate);
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
        var todayJson = JsonConvert.SerializeObject(context.FactDate);

        var output = template
            .Replace("{{TreeData}}", treeJson)
            .Replace("{{global_non_working}}", globalJson)
            .Replace("{{resource_non_working}}", resourceJson)
            .Replace("{{fact_date}}", todayJson);

        File.WriteAllText(outputPath, output, Encoding.UTF8);
    }

    // Обходит дерево сверху вниз и строит по узлу на каждую задачу.
    // Цвет бара считается заранее для всего дерева разом
    // (см. TaskColorPalette): свой цвет на эпик, оттенки этого цвета - на
    // задачи внутри эпика; здесь остаётся только разложить готовые цвета
    // по узлам.
    private static TaskNode BuildNode(
        ProjectDto project,
        TaskDto task,
        IReadOnlyDictionary<TaskDto, string> colors,
        string? epicJiraKey,
        string? epicJiraUrl)
    {
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

        // Для детей "ближайший предок с Jira-ключом" - это сама задача, если
        // у неё есть свой JiraKey, иначе то, что пришло сверху (пробрасываем
        // дальше вниз, пока не встретим следующего владельца ключа).
        var epicKeyForChildren = !string.IsNullOrWhiteSpace(task.JiraKey) ? task.JiraKey : epicJiraKey;
        var epicUrlForChildren = !string.IsNullOrWhiteSpace(task.JiraKey) ? jiraUrl : epicJiraUrl;

        var children = task.Children
            .Select(child => BuildNode(project, child, colors, epicKeyForChildren, epicUrlForChildren))
            .ToList();

        return new TaskNode(
            task.Id,
            task.Name,
            task.JiraKey,
            jiraUrl,
            epicJiraKey,
            epicJiraUrl,
            assignee,
            task.Plan?.PlannedStart,
            task.Plan?.PlannedFinish,
            statusLabel,
            colors.TryGetValue(task, out var color) ? color : TaskColorPalette.StructuralColor,
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
        // ближайший предок (не сама задача), у которого есть свой JiraKey -
        // "родительский эпик", чтобы было видно, к чему относится задача,
        // не листая дерево вверх и не разворачивая свёрнутые узлы
        string? EpicJiraKey,
        string? EpicJiraUrl,
        string? Assignee,
        DateOnly? Start,
        DateOnly? End,
        string? StatusLabel,
        // цвет бара (hex): у эпика - его собственный цвет, у задач внутри
        // эпика - оттенки того же цвета (см. TaskColorPalette)
        string Color,
        List<TaskNode> Children
    );

    public sealed record TreeContext(
        TaskNode Root,
        DateOnly[] GlobalNonWorking,
        Dictionary<string, DateOnly[]> ResourceNonWorking,
        // Дата среза "на сегодня" (ProjectDto.FactDate) - используется только
        // для кнопки "на сегодня" в шаблоне; сама временная ось по-прежнему
        // всегда начинается с начала плана, а не с этой даты.
        DateOnly? FactDate
    );
}
