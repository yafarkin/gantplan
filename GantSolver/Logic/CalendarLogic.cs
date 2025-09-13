using System.Collections.Immutable;
using GantPlan.Dtos;

namespace GantPlan.Logic;

public sealed class CalendarLogic
{
    private readonly DateOnly _startDate;

    private readonly ImmutableArray<bool> _days;
    
    public ImmutableHashSet<int> NonWorkingDays { get; }

    public CalendarLogic(int horizon, DateOnly startDate, CalendarDto? globalCalendar, CalendarDto? resourceCalendar)
    {
        _startDate = startDate;
        
        var nonWorkingDays = new HashSet<int>();
        var days = new bool[horizon];

        var d = startDate;
        
        for (var i = 0; i < horizon; i++)
        {
            var workDay = d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            
            if (workDay && globalCalendar?.NonWorkingDays is not null)
            {
                var p = globalCalendar.NonWorkingDays.SingleOrDefault(x => d >= x.From && d <= x.To);
                if (p is not null)
                {
                    workDay = false;
                }
            }
            
            if (!workDay && globalCalendar?.WorkingDays is not null)
            {
                var p = globalCalendar.WorkingDays.SingleOrDefault(x => d >= x.From && d <= x.To);
                if (p is not null)
                {
                    workDay = true;
                }
            }
            
            if (workDay && resourceCalendar?.NonWorkingDays is not null)
            {
                var p = resourceCalendar.NonWorkingDays.SingleOrDefault(x => d >= x.From && d <= x.To);
                if (p is not null)
                {
                    workDay = false;
                }
            }

            if (!workDay && resourceCalendar?.WorkingDays is not null)
            {
                var p = resourceCalendar.WorkingDays.SingleOrDefault(x => d >= x.From && d <= x.To);
                if (p is not null)
                {
                    workDay = true;
                }
            }
            
            days[i] = workDay;
            
            if (!workDay)
            {
                nonWorkingDays.Add(i);
            }
            
            d = d.AddDays(1);
        }
        
        _days = [..days];
        NonWorkingDays = [..nonWorkingDays];
    }

    public int CalcWorkingDaysCount(DateOnly start, DateOnly end)
    {
        if (start < _startDate || end > _startDate.AddDays(_days.Length))
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var result = 0;
        var s = start.DayNumber - _startDate.DayNumber;
        var e = end.DayNumber - _startDate.DayNumber;
        for (var i = s; i <= e; i++)
        {
            if (_days[i])
            {
                result++;
            }
        }

        return result;
    }
}