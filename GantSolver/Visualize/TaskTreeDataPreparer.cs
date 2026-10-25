using System.Text;
using System.Text.RegularExpressions;
using GantPlan.Dtos;
using GantPlan.Logic;
using Newtonsoft.Json;

namespace GantPlan.Visualize;

// Готовит данные для task_tree_template.html: в отличие от прежнего
// плоского "по одному ряду на исполнителя", сюда уходит дерево задач как
// есть, вместе с уже посчитанными датами (Solver.PostFillContainerTasks
// заполняет Plan и у контейнеров тоже - агрегатом по детям, так что
// пересчитывать даты для групп здесь не нужно).
public static class TaskTreeDataPreparer
{
    // d3 лежит рядом с шаблоном и встраивается прямо в HTML - так
    // timeline.html открывается с диска и без интернета.
    private const string D3FileName = "d3.v7.9.0.min.js";
    private const string D3CdnTag = "<script src=\"https://cdn.jsdelivr.net/npm/d3@7.9.0/dist/d3.min.js\"></script>";

    public static TreeContext Prepare(ProjectDto project)
    {
        var colors = TaskColorPalette.Build(project.RootTask);
        var root = BuildNode(project, project.RootTask, colors, epicJiraKey: null, epicJiraUrl: null);

        // Нерабочие дни считаются тем же CalendarLogic, что и у солвера
        // (выходные, праздники, рабочие переносы, личные отпуска и личные
        // рабочие дни), а не пересчитываются в шаблоне по-своему - иначе
        // диаграмма расходится с планом: например, штрихует рабочую субботу,
        // в которую солвер законно поставил работу.
        var range = PlanRange(project.RootTask);
        var globalNonWorking = NonWorkingDays(project, range, null);
        var resourceNonWorking = project.Resources
            .ToDictionary(ResourceKey, r => NonWorkingDays(project, range, r.Calendar));

        // AvailFrom/AvailTo - только у тех, у кого задана хотя бы одна
        // граница: шаблон рисует их отдельно от календаря (линия на границе
        // и бледная заливка), а не штриховкой каждого дня.
        var resourceAvailability = project.Resources
            .Where(r => r.AvailFrom is not null || r.AvailTo is not null)
            .ToDictionary(ResourceKey, r => new Availability(r.AvailFrom, r.AvailTo));

        return new TreeContext(root, globalNonWorking, resourceNonWorking, resourceAvailability, project.FactDate);
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

        // EscapeHtml - для всех данных, не только для дерева: имя задачи или
        // ресурса с "</script>" иначе разорвало бы встроенный скрипт.
        var jsonSettings = new JsonSerializerSettings {StringEscapeHandling = StringEscapeHandling.EscapeHtml};

        var d3Path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(templatePath))!, D3FileName);
        var d3Script = File.Exists(d3Path)
            ? "<script>" + File.ReadAllText(d3Path, Encoding.UTF8) + "</script>"
            : D3CdnTag;

        var values = new Dictionary<string, string>
        {
            ["d3_script"] = d3Script,
            ["TreeData"] = JsonConvert.SerializeObject(context.Root, jsonSettings),
            ["global_non_working"] = JsonConvert.SerializeObject(context.GlobalNonWorking, jsonSettings),
            ["resource_non_working"] = JsonConvert.SerializeObject(context.ResourceNonWorking, jsonSettings),
            ["resource_availability"] = JsonConvert.SerializeObject(context.ResourceAvailability, jsonSettings),
            ["fact_date"] = JsonConvert.SerializeObject(context.FactDate, jsonSettings)
        };

        // Один проход по шаблону, а не цепочка Replace: иначе подставленные
        // данные (например, задача с названием "{{fact_date}}") сами
        // подменялись бы следующими Replace.
        var output = Regex.Replace(template, @"\{\{(\w+)\}\}",
            m => values.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

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
            {Plan.AwaitingClosure: true} => "ждёт подтверждения закрытия",
            _ => null
        };

        // приглушаются только задачи, которые уже не в работе; "ждёт
        // подтверждения закрытия" - наоборот, требует внимания
        var muted = task.Disabled || task.Fact?.IsPaused == true || task.Fact?.IsFinished == true;

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
            muted,
            !task.HasChild && task.Plan?.AwaitingClosure == true,
            colors.TryGetValue(task, out var color) ? color : TaskColorPalette.StructuralColor,
            children);
    }

    private static string ResourceKey(ResourceDto resource) => $"{resource.Name}-{resource.Role}";

    // Диапазон дат, который покрывает таймлайн: от самого раннего начала до
    // самого позднего окончания по всему дереву (шаблон строит ось так же).
    private static (DateOnly From, DateOnly To)? PlanRange(TaskDto root)
    {
        var dates = new List<DateOnly>();

        void Walk(TaskDto task)
        {
            if (task.Plan?.PlannedStart is { } start) dates.Add(start);
            if (task.Plan?.PlannedFinish is { } finish) dates.Add(finish);
            foreach (var child in task.Children) Walk(child);
        }

        Walk(root);

        return dates.Count == 0 ? null : (dates.Min(), dates.Max());
    }

    private static DateOnly[] NonWorkingDays(ProjectDto project, (DateOnly From, DateOnly To)? range, CalendarDto? resourceCalendar)
    {
        if (range is not { } r)
        {
            return [];
        }

        var days = r.To.DayNumber - r.From.DayNumber + 1;
        var calendar = new CalendarLogic(days, r.From, project.GlobalCalendar, resourceCalendar);

        return Enumerable.Range(0, days)
            .Where(i => !calendar.IsWorkingDay(i))
            .Select(i => r.From.AddDays(i))
            .ToArray();
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
        // приглушить строку и бар (выключена, на паузе, завершена)
        bool Muted,
        // по плану уже должна была закончиться, но закрытие не подтверждено
        // (см. TaskPlanDto.AwaitingClosure) - бар выделяется и в свёрнутом
        // треке исполнителя, где метки статуса не видно
        bool AwaitingClosure,
        // цвет бара (hex): у эпика - его собственный цвет, у задач внутри
        // эпика - оттенки того же цвета (см. TaskColorPalette)
        string Color,
        List<TaskNode> Children
    );

    public sealed record Availability(DateOnly? From, DateOnly? To);

    public sealed record TreeContext(
        TaskNode Root,
        // итоговые общие нерабочие дни на диапазоне плана: выходные и
        // праздники за вычетом рабочих переносов (см. CalendarLogic)
        DateOnly[] GlobalNonWorking,
        // итоговые нерабочие дни каждого исполнителя (ключ - ResourceKey):
        // общий календарь плюс личный - отпуска, личные рабочие дни
        Dictionary<string, DateOnly[]> ResourceNonWorking,
        // границы доступности исполнителя (ResourceDto.AvailFrom/AvailTo,
        // обе включительно; ключ - ResourceKey) - только у тех, у кого они заданы
        Dictionary<string, Availability> ResourceAvailability,
        // Дата среза "на сегодня" (ProjectDto.FactDate) - линия "сегодня" и
        // кнопка "на сегодня" в шаблоне; сама временная ось по-прежнему
        // всегда начинается с начала плана, а не с этой даты.
        DateOnly? FactDate
    );
}
