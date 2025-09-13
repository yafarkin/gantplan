namespace GantPlan.Dtos;

public sealed record CalendarPeriod
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
}