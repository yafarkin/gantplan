using Newtonsoft.Json;

namespace GantPlan.Dtos.OldVisualize;

public sealed record RootDto
{
    [JsonProperty("tasks")]
    public required OldTaskDto[] Tasks { get; init; } = null!;

    [JsonProperty("resources")]
    public required OldResourceDto[] Resources { get; init; } = null!;

    [JsonProperty("global_non_working")]
    public required DateOnly[] GlobalNonWorking { get; init; } = null!;

    [JsonProperty("resource_non_working")]
    public required Dictionary<string, DateOnly[]> ResourceNonWorking { get; init; } = null!;
}

public sealed record OldTaskDto
{
    [JsonProperty("id")]
    public required string Id { get; init; } = null!;

    [JsonProperty("name")]
    public required string Name { get; init; } = null!;

    [JsonProperty("start")]
    public required DateOnly Start { get; init; }

    [JsonProperty("end")]
    public required DateOnly End { get; init; }

    [JsonProperty("duration")]
    public required int? Duration { get; init; }
    
    [JsonProperty("buffer")]
    public int? Buffer { get; init; }

    [JsonProperty("employee")]
    public required string? Employee { get; init; }

    [JsonProperty("predecessors")]
    public required string[]? Predecessors { get; init; }

    [JsonProperty("tasks")]
    public required OldTaskDto[]? Tasks { get; init; }
}

public sealed record OldResourceDto
{
    [JsonProperty("name")]
    public required string Name { get; init; } = null!;

    [JsonProperty("start")]
    public DateOnly Start { get; init; }

    [JsonProperty("end")]
    public DateOnly End { get; init; }
    
    [JsonProperty("totalDays")]
    public int TotalDays { get; init; }

    [JsonProperty("workDays")]
    public int WorkDays { get; init; }
}