using Newtonsoft.Json;

namespace GantPlan.Dtos;

public sealed record TaskPlanDto
{
    // Исполнитель; null - если неизвестен (например, у закрытой задачи,
    // по которой нигде не записано, кто её делал).
    public string? ResourceName { get; set; }
    public DateOnly? PlannedStart { get; set; }
    public DateOnly? PlannedFinish { get; set; }

    /// <summary>
    /// По плану задача должна была закончиться не позже FactDate, но закрытие
    /// (Completed) не подтверждено вручную - поэтому солвер считает её ещё
    /// не сделанной и тянет до ближайшего рабочего дня после FactDate.
    /// </summary>
    [JsonProperty(DefaultValueHandling = DefaultValueHandling.Ignore)]
    public bool AwaitingClosure { get; set; }
}
