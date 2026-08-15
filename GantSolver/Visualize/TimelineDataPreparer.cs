using System.Text;
using GantPlan.Dtos;
using Newtonsoft.Json;

namespace GantPlan.Visualize;

// Готовит данные для timeline_template.html прямо из ProjectDto - раньше
// между ними стоял промежуточный "старый формат" (RootDto/OldTaskDto,
// GantPlan.Mapping.ProjectDtoMapper), доставшийся от более ранней версии
// визуализации; сам HTML-шаблон его не видел (получает только
// TimelineContext ниже), так что от него можно было избавиться, ничего не
// трогая в timeline_template.html.
public sealed class TimelineDataPreparer
{
    /// <summary>
    /// Собирает контекст данных для шаблона прямо из решённого проекта:
    /// по одной Activity на каждую запланированную листовую задачу (без
    /// Plan задача пропускается - значит, не решалась или ещё не дошла
    /// до этого), сгруппированные по исполнителю.
    /// </summary>
    public static TimelineContext Prepare(ProjectDto project)
    {
        var activities = CollectActivities(project);

        if (activities.Count == 0)
        {
            throw new ArgumentException("В проекте нет запланированных задач для отображения");
        }

        var persons = activities
            .GroupBy(a => a.Assignee)
            .Select(g => new PersonActivity(g.Key, g.Select(a => a.Activity).ToList()))
            .ToList();

        var globalNonWorking = ExpandPeriods(project.GlobalCalendar?.NonWorkingDays);
        var resourceNonWorking = project.Resources
            .Where(r => r.Calendar is not null)
            .ToDictionary(ResourceKey, r => ExpandPeriods(r.Calendar!.NonWorkingDays));

        return new TimelineContext(persons, globalNonWorking, resourceNonWorking);
    }

    /// <summary>
    ///     Генерирует итоговый HTML по шаблону.
    /// </summary>
    public static void GenerateHtml(
        string templatePath,
        string outputPath,
        TimelineContext context)
    {
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("Шаблон не найден", templatePath);
        }

        var template = File.ReadAllText(templatePath, Encoding.UTF8);

        // Сериализуем в JSON для подстановки в HTML
        var personDataJson = JsonConvert.SerializeObject(
            context.PersonActivitiesData,
            new JsonSerializerSettings {StringEscapeHandling = StringEscapeHandling.EscapeHtml}
        );
        var globalJson = JsonConvert.SerializeObject(context.GlobalNonWorking);
        var resourceJson = JsonConvert.SerializeObject(context.ResourceNonWorking);

        var output = template
            .Replace("{{PersonActivitiesData}}", personDataJson)
            .Replace("{{global_non_working}}", globalJson)
            .Replace("{{resource_non_working}}", resourceJson);

        File.WriteAllText(outputPath, output, Encoding.UTF8);
    }

    // Обходит дерево задач один раз и для каждой запланированной листовой
    // задачи строит и Activity (для шаблона), и её исполнителя (для
    // группировки) - раньше это было два прохода через промежуточный формат.
    private static List<(string Assignee, Activity Activity)> CollectActivities(ProjectDto project)
    {
        var result = new List<(string, Activity)>();
        Walk(project.RootTask, []);
        return result;

        void Walk(TaskDto task, List<string> ancestorChain)
        {
            var chain = new List<string>(ancestorChain) { FormatTaskLabel(task) };

            if (task.HasChild)
            {
                foreach (var child in task.Children)
                {
                    Walk(child, chain);
                }

                return;
            }

            if (task.Plan is null)
            {
                return;
            }

            var start = task.Plan.PlannedStart ?? throw new InvalidOperationException($"PlannedStart is required for task {task.Id}");
            var end = task.Plan.PlannedFinish ?? throw new InvalidOperationException($"PlannedFinish is required for task {task.Id}");
            var resource = project.Resources.Single(r => r.Name == task.Plan.ResourceName);

            var activity = new Activity(task.Id, start, end, string.Join(" > ", chain));

            result.Add((ResourceKey(resource), activity));
        }
    }

    private static string FormatTaskLabel(TaskDto task) => string.IsNullOrWhiteSpace(task.JiraKey)
        ? $"{task.Id} — {task.Name}"
        : $"{task.Id} — {task.JiraKey}: {task.Name}";

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

    public sealed record Activity(
        string TaskKey,
        DateOnly Start,
        DateOnly End,
        string FullName
    );

    public sealed record PersonActivity(
        string Assignee,
        List<Activity> Activities
    );

    public sealed record TimelineContext(
        List<PersonActivity> PersonActivitiesData,
        DateOnly[] GlobalNonWorking,
        Dictionary<string, DateOnly[]> ResourceNonWorking
    );
}
