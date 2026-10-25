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

    /// <summary>
    /// Запись дописана солвером по FactDate (автоматический прогресс: старт
    /// по плану, остаток на дату среза), а не введена вручную. Такие записи
    /// не участвуют в планировании: перед каждым решением они удаляются и
    /// считаются заново. Закрытие (Completed) солвер сам никогда не пишет -
    /// его подтверждают только вручную.
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool IsAuto { get; set; }
}