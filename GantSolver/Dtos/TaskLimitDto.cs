using GantPlan.Dtos.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GantPlan.Dtos;

public sealed record TaskLimitDto
{
    /// <summary>
    /// Приоритет работ, чем меньше значение, тем раньше по графику.
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// Длительность работ, в ч/д. Должно быть задано или это поле, или размер майки <see cref="TShirt"/>
    /// </summary>
    public int? Duration { get; init; }
    
    /// <summary>
    /// Опциональное буфферное время в ч/д. Просто складывается, позднее будет рисоваться на графике.
    /// По факту, отражает нашу неуверенность в оценке.
    /// </summary>
    public int? Buffer { get; init; }

    /// <summary>
    /// Роль, которая может выполнить работу. Задано должно быть или это поле, или <see cref="ResourceName"/>.
    /// </summary>
    public string? ResourceRole { get; init; }

    /// <summary>
    /// Конкретный исполнитель работы, если мы его знаем. Или это поле, или <see cref="ResourceRole"/>.
    /// </summary>
    public string? ResourceName { get; init; }
    
    /// <summary>
    /// Размер майки. Надо при планировании работ ориентироваться на это поле. Должно быть задано или это поле, или <see cref="Duration"/>
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public TShirtType? TShirt { get; init; }

    /// <summary>
    /// Опционально, список ключей тех задач, которые должны быть выполнены до старта этой задачи.
    /// </summary>
    public ICollection<string> PredecessorIds { get; set; } = [];
}