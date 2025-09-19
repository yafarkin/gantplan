using GantPlan.Dtos.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GantPlan.Dtos;

public sealed record TaskLimitDto
{
    /// <summary>
    /// Work priority – the smaller the number, the sooner it appears on the schedule.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Work duration in days.
    /// Either this field **or** the <see cref="TShirt"/> size must be specified.
    /// </summary>
    public int? Duration { get; init; }
    
    /// <summary>
    /// Optional buffer time in person‑days.
    /// It’s simply added to the schedule and will be visualised on the chart later.
    /// Essentially it represents our uncertainty about the estimate.
    /// </summary>
    public int? Buffer { get; init; }

    /// <summary>
    /// The role capable of performing the task.
    /// Either this field **or** <see cref="ResourceName"/> must be specified.
    /// </summary>
    public string? ResourceRole { get; init; }

    /// <summary>
    /// The specific assignee for the task, if known.
    /// Either this field **or** <see cref="ResourceRole"/> must be specified.
    /// </summary>
    public string? ResourceName { get; init; }
    
    /// <summary>
    /// T‑shirt size – the metric to use when estimating work.
    /// Either this field **or** <see cref="Duration"/> must be specified.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public TShirtType? TShirt { get; init; }

    /// <summary>
    /// (Optional) List of keys for tasks that must finish before this one can start.
    /// </summary>
    public ICollection<string> PredecessorIds { get; set; } = [];
    
    /// <summary>
    /// (Optional) Due date for task.
    /// </summary>
    public DateOnly? DueDate { get; set; }
    
    /// <summary>
    /// (Optional) The date after which the task can start.
    /// </summary>
    public DateOnly? StartAfter { get; set; }
}