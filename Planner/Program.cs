using System.Diagnostics;
using System.Runtime.InteropServices;
using GantPlan.Dtos;
using GantPlan.Logic;
using GantPlan.Visualize;
using Google.OrTools.Sat;
using Newtonsoft.Json;
using Planner.SourcePlans;

// TODO:
//   • Add a production model.
//   • Add a review step – assign the task to a different reviewer,
//     not the same person who performed the dev step.

ProjectDto project;

if (File.Exists("project.json"))
{
    Console.WriteLine("Reading project.json");
    var inputJson = File.ReadAllText("project.json");
    project = JsonConvert.DeserializeObject<ProjectDto>(inputJson)!;

    project.FactDate = new DateOnly(2026, 1, 17);
}
else
{
    project = DemoProject.Build();
}

var s = new Solver();
var solved = s.Solve(project, 30);

Console.WriteLine(solved
    ? $"Solver: {s.Status}, objective {s.ObjectiveValue:F0}, horizon {s.Horizon} days, {s.WallTimeSeconds:F1}s"
    : $"Solver: {s.Status}, horizon {s.Horizon} days, {s.WallTimeSeconds:F1}s");

if (s.TieBreakStatus is { } tieBreak and not CpSolverStatus.Optimal)
{
    Console.WriteLine($"  Выбор между равноценными планами не завершён ({tieBreak}) - порядок равноценных задач может меняться.");
}

if (s.Status == CpSolverStatus.Feasible)
{
    Console.WriteLine("  Оптимальность не доказана за лимит времени - план может отличаться от прогона к прогону.");
}

if (!solved)
{
    Console.WriteLine(s.Status == CpSolverStatus.Infeasible
        ? "  Ограничения противоречат друг другу: проверьте DueDate/StartAfter, AvailFrom/AvailTo, " +
          "зависимости и уже начатые задачи (их старт и исполнитель зафиксированы)."
        : "  За лимит времени не найдено ни одного плана - увеличьте лимит или упростите модель.");
    // Останавливаемся здесь, а не проваливаемся дальше: project - тот же
    // (непроверенный/неполный) объект, каким был до Solve, перезаписывать
    // им уже существующий валидный project.json нечем, а генерировать
    // timeline.html без Plan-дат - только вводить в заблуждение. Ненулевой
    // код возврата - чтобы вызывающий скрипт (в т.ч. CI) мог отличить
    // "не решилось" от "всё нормально", а не только читать текст в консоли.
    Console.WriteLine("Sorry, I couldn't solve this project");
    return 1;
}

foreach (var warning in s.Warnings)
{
    Console.WriteLine($"  ! {warning}");
}

// Закрытие задач подтверждается только вручную (запись Completed в Fact) -
// пока его нет, задача считается несделанной и держит исполнителя и
// последователей (см. Solver.CalcTaskEnd).
if (s.AwaitingClosure.Count > 0)
{
    Console.WriteLine($"Ждут подтверждения закрытия на {project.FactDate:dd.MM.yyyy} ({s.AwaitingClosure.Count}):");
    foreach (var task in s.AwaitingClosure)
    {
        var key = string.IsNullOrWhiteSpace(task.JiraKey) ? task.Id : task.JiraKey;
        Console.WriteLine($"  {key,-12} {task.Name} ({task.Plan!.ResourceName})");
    }
}

// Берём календарный квартал, в который попадает начало плана.
var (qs, qe) = CalendarQuarter.Containing(project.ProjectStart);

var quarterReport = QuarterDistributionExporter.Build(project, qs, qe);
Console.WriteLine(quarterReport);

var dailyPlan = DailyPlanExporter.Build(project, qs, qe);
await File.WriteAllTextAsync("daily_plan.txt", dailyPlan);

var jsonSettings = new JsonSerializerSettings
{
    NullValueHandling = NullValueHandling.Ignore,
    Formatting = Formatting.Indented
};

var outputJson = JsonConvert.SerializeObject(project, jsonSettings);
await File.WriteAllTextAsync("project.json", outputJson);

var treeContext = TaskTreeDataPreparer.Prepare(project);
TaskTreeDataPreparer.GenerateHtml("Templates/task_tree_template.html", "timeline.html", treeContext);
RevealFile("timeline.html");

return 0;

static void RevealFile(string fullPath)
{
    if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
    {
        return;
    }

    ProcessStartInfo psi;

    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        psi = new ProcessStartInfo(fullPath)
        {
            UseShellExecute = true
        };
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
    {
        psi = new ProcessStartInfo
        {
            FileName = "open",
            ArgumentList = {"-R", fullPath},
            UseShellExecute = false
        };
    }
    else
    {
        psi = new ProcessStartInfo
        {
            FileName = "xdg-open",
            ArgumentList = { fullPath },
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    Process.Start(psi);
}
