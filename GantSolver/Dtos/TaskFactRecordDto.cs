using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GantPlan.Dtos;

public sealed record TaskFactRecordDto
{
    /// <summary>
    /// Record date. The value depends on the type of <see cref="TaskFactRecordType"/>.
    /// * Started – work started at this date.
    /// * Completed/Cancelled – work finished at this date.
    /// * InProgress – snapshot date for this record, i.e., work is ongoing.
    /// </summary>
    public DateOnly RecordedAt { get; set; }
    
    [JsonConverter(typeof(StringEnumConverter))]
    public TaskFactRecordType Type { get; set; }
    
    public string? ResourceName { get; set; }
    
    /// <summary>
    /// Duration. Absolute new value.
    /// Indicates how many hours/days are still required to complete the task.
    /// </summary>
    public int? Duration { get; set; }
    
    public string? Comments { get; init; }
}