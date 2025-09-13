using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GantPlan.Dtos;

public sealed record TaskFactRecordDto
{
    /// <summary>
    /// Дата записи. Значение зависит от типа <see cref="TaskFactRecordType"/>
    /// Started - работа началась в эту дату.
    /// Completed/Cancelled - завершилась
    /// InProgress - дата среза на эту запись, т.е работа идёт
    /// </summary>
    public DateOnly RecordedAt { get; set; }
    
    [JsonConverter(typeof(StringEnumConverter))]
    public TaskFactRecordType Type { get; set; }
    
    public string? ResourceName { get; set; }
    
    /// <summary>
    /// Длительность. Абсолютное новое значение.
    /// Показывает, сколько требуется ещё ч/д для завершения задачи
    /// </summary>
    public int? Duration { get; set; }
    
    public string? Comments { get; init; }
}