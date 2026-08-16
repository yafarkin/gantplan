using System.Diagnostics;
using System.Runtime.InteropServices;
using GantPlan.Dtos;
using GantPlan.Logic;
using GantPlan.Visualize;
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
if (!s.Solve(project, 30))
{
    // Останавливаемся здесь, а не проваливаемся дальше: project - тот же
    // (непроверенный/неполный) объект, каким был до Solve, перезаписывать
    // им уже существующий валидный project.json нечем, а генерировать
    // timeline.html без Plan-дат - только вводить в заблуждение. Ненулевой
    // код возврата - чтобы вызывающий скрипт (в т.ч. CI) мог отличить
    // "не решилось" от "всё нормально", а не только читать текст в консоли.
    Console.WriteLine("Sorry, I couldn't solve this project");
    return 1;
}

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
