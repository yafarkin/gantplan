namespace GantPlan.Dtos;

public sealed record ResourceDto
{
    public string Role { get; init; } = null!;

    public string Name { get; init; } = null!;

    public DateOnly? AvailFrom { get; init; }

    public DateOnly? AvailTo { get; init; }

    /// <summary>
    /// Resource performance.
    /// </summary>
    public int Percent { get; init; } = 100;

    public CalendarDto? Calendar { get; init; }
    
    /// <summary>
    /// Confidence in estimations, when TShirt used. 100% - lower bound, 0% - upper bound.
    /// </summary>
    public int Confidence { get; init; } = 0;
}