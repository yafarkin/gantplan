using GantPlan.Dtos.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GantPlan.Dtos;

public sealed record TaskDto
{
    public string Id { get; init; } = null!;

    public string? JiraKey { get; init; }
    public string Name { get; init; } = null!;
    
    public string? Comments { get; init; }

    public TaskLimitDto? Limit { get; init; } = null!;

    public TaskPlanDto? Plan { get; set; }
    public TaskFactDto? Fact { get; set; }
    
    [JsonConverter(typeof(StringEnumConverter))]
    public WorkType? WorkType { get; set; }
    
    public ICollection<TaskDto> Children { get; init; } = [];

    public bool Disabled { get; init; }

    [JsonIgnore]
    public bool HasChild => Children.Any();

    [JsonIgnore]
    public bool CanSkipTask => Disabled || Fact?.IsPaused == true || Fact?.IsFinished == true;
}