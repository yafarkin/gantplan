using GantPlan.Dtos;

namespace GantPlan.Visualize;

// Общая календарная логика: является ли день рабочим и сколько рабочих дней
// в интервале. Приоритет как в CalendarLogic: по умолчанию будни рабочие;
// дальше глобальные NonWorking/Working; в самом конце персональные календари.
internal static class WorkingDayCalculator
{
    public static bool IsWorkingDay(
        DateOnly day,
        DateOnly[] globalNonWorking, DateOnly[] globalWorking,
        DateOnly[] resourceNonWorking, DateOnly[] resourceWorking)
    {
        var isWorking = day.DayOfWeek != DayOfWeek.Saturday && day.DayOfWeek != DayOfWeek.Sunday;

        if (globalNonWorking.Contains(day)) isWorking = false;
        if (globalWorking.Contains(day)) isWorking = true;
        if (resourceNonWorking.Contains(day)) isWorking = false;
        if (resourceWorking.Contains(day)) isWorking = true;

        return isWorking;
    }

    public static int CountWorkingDays(
        DateOnly start, DateOnly end,
        DateOnly[] globalNonWorking, DateOnly[] globalWorking,
        DateOnly[] resourceNonWorking, DateOnly[] resourceWorking)
    {
        var count = 0;
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            if (IsWorkingDay(day, globalNonWorking, globalWorking, resourceNonWorking, resourceWorking))
            {
                count++;
            }
        }

        return count;
    }

    public static DateOnly[] ExpandPeriods(ICollection<CalendarPeriod>? periods)
    {
        var result = new List<DateOnly>();
        if (periods is not null)
        {
            foreach (var period in periods)
            {
                var day = period.From;
                while (day <= period.To)
                {
                    result.Add(day);
                    day = day.AddDays(1);
                }
            }
        }

        return result.ToArray();
    }
}