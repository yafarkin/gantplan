using System.Text;
using Newtonsoft.Json;

namespace GantPlan.Dtos.OldVisualize;

public sealed class TimelineDataPreparer
{
    /// <summary>
    /// Принимает готовый RootDto и собирает контекст данных для шаблона.
    /// </summary>
    public static TimelineContext Prepare(RootDto root)
    {
        if (root.Tasks == null || root.Tasks.Length == 0)
        {
            throw new ArgumentException("RootDto.Tasks пуст или не инициализирован");
        }

        // Берем задачи первого уровня
        var topTasks = root.Tasks[0].Tasks ?? [];
        var flat = FlattenTasks(topTasks);

        // Группируем по исполнителю и конвертируем в Activity
        var grouped = flat
            .GroupBy(a => a.Assignee)
            .ToDictionary(
                g => g.Key,
                g => g.Select(f => new Activity(
                        f.TaskKey,
                        f.Status,
                        f.Start,
                        f.End,
                        f.Buffer,
                        f.FullName))
                    .ToList()
            );

        var persons = grouped
            .Select(kvp => new PersonActivity(kvp.Key, kvp.Value))
            .ToList();

        return new TimelineContext(
            persons,
            root.GlobalNonWorking,
            root.ResourceNonWorking
        );
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

    private static List<FlatActivity> FlattenTasks(
        OldTaskDto[] tasks,
        List<(string id, string name)>? parentChain = null)
    {
        var list = new List<FlatActivity>();
        parentChain ??= new List<(string id, string name)>();

        foreach (var t in tasks)
        {
            var chain = new List<(string id, string name)>(parentChain) {(t.Id, t.Name)};

            if (!string.IsNullOrEmpty(t.Employee))
            {
                var fullName = string.Join(" > ", chain.Select(c => $"{c.id} — {c.name}"));
                
                list.Add(new FlatActivity(
                    t.Employee,
                    t.Id,
                    t.Name,
                    t.Start,
                    t.End,
                    t.Buffer,
                    fullName
                ));
            }

            if (t.Tasks is {Length: > 0})
            {
                list.AddRange(FlattenTasks(t.Tasks, chain));
            }
        }

        return list;
    }

    public sealed record Activity(
        string TaskKey,
        string Status,
        DateOnly Start,
        DateOnly End,
        int? Buffer,
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

    // Внутренний тип для плоских записей
    private sealed record FlatActivity(
        string Assignee,
        string TaskKey,
        string Status,
        DateOnly Start,
        DateOnly End,
        int? Buffer,
        string FullName
    );
}