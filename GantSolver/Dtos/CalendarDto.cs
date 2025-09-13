namespace GantPlan.Dtos;

public sealed record CalendarDto
{
    public ICollection<CalendarPeriod>? NonWorkingDays { get; init; }

    public ICollection<CalendarPeriod>? WorkingDays { get; init; }
}