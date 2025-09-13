namespace GantPlan.Dtos;

public sealed record ResourceDto
{
    public string Role { get; init; } = null!;

    public string Name { get; init; } = null!;

    public DateOnly? AvailFrom { get; init; }

    public DateOnly? AvailTo { get; init; }

    public int Percent { get; init; } = 100;

    public CalendarDto? Calendar { get; init; }
}