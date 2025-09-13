using System.Diagnostics;
using System.Runtime.InteropServices;
using GantPlan.Dtos;
using GantPlan.Dtos.OldVisualize;
using GantPlan.Logic;
using GantPlan.Mapping;
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
    Console.WriteLine("Sorry, I couldn't solve this project");
}

var jsonSettings = new JsonSerializerSettings
{
    NullValueHandling = NullValueHandling.Ignore,
    Formatting = Formatting.Indented
};

var outputJson = JsonConvert.SerializeObject(project, jsonSettings);
await File.WriteAllTextAsync("project.json", outputJson);

var oldFormat = ProjectDtoMapper.MapToRootDto(project);

var timelineContext = TimelineDataPreparer.Prepare(oldFormat);
TimelineDataPreparer.GenerateHtml("Templates/timeline_template.html", "timeline.html", timelineContext);
RevealFile("timeline.html");

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
