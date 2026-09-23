namespace GantPlan.Visualize;

public static class CalendarQuarter
{
    public static (DateOnly Start, DateOnly End) Containing(DateOnly date)
    {
        var firstMonth = ((date.Month - 1) / 3) * 3 + 1;
        var start = new DateOnly(date.Year, firstMonth, 1);
        return (start, start.AddMonths(3).AddDays(-1));
    }
}
