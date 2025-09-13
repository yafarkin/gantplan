namespace GantPlan.Dtos;

public sealed record ProjectDto
{
    public DateOnly ProjectStart { get; init; }
    public DateOnly? FactDate { get; set; }

    public TaskDto RootTask { get; init; } = null!;

    public ICollection<ResourceDto> Resources { get; init; } = [];

    public CalendarDto? GlobalCalendar { get; init; }
}