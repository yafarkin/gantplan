namespace GantPlan.Dtos;

public sealed record ProjectDto
{
    public DateOnly ProjectStart { get; init; }
    public DateOnly? FactDate { get; set; }

    public TaskDto RootTask { get; init; } = null!;

    public ICollection<ResourceDto> Resources { get; init; } = [];

    public CalendarDto? GlobalCalendar { get; init; }

    /// <summary>
    /// Базовый URL для ссылок на задачи в Jira, например
    /// "https://mycompany.atlassian.net/browse/" (со слэшем в конце - к нему
    /// просто конкатенируется <see cref="TaskDto.JiraKey"/>). Не участвует
    /// ни в одном ограничении солвера - используется только при
    /// визуализации, чтобы сделать номер задачи кликабельной ссылкой. Если
    /// не задан, JiraKey показывается обычным текстом.
    /// </summary>
    public string? BaseJiraUrl { get; set; }
}