using Newtonsoft.Json;

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

    /// <summary>
    /// Произвольные метки задачи (например, "WorkType" -> "Business",
    /// "IsOkr" -> "true"). Не участвуют ни в одном ограничении солвера -
    /// нужны только для последующей статистики по дереву задач. Ключ,
    /// заданный у родителя и отсутствующий у ребёнка, наследуется вниз
    /// (см. <see cref="GantPlan.Logic.TaskAttributeInheritance"/>) - чтобы
    /// добавить новое измерение статистики, не нужно менять код солвера,
    /// достаточно проставить новый ключ в дереве. См. также
    /// <see cref="TaskTagKeys"/> для уже используемых ключей.
    /// </summary>
    public Dictionary<string, string>? Tags { get; set; }

    public ICollection<TaskDto> Children { get; init; } = [];

    public bool Disabled { get; set; }

    [JsonIgnore]
    public bool HasChild => Children.Any();

    [JsonIgnore]
    public bool CanSkipTask => Disabled || Fact?.IsPaused == true || Fact?.IsFinished == true;
}