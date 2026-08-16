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
            // Порядок стадий - это и есть приоритет: сначала будний/выходной
            // по умолчанию, потом глобальный календарь (гос. праздники,
            // общие для всех командировки и т.п.), потом персональный - он
            // применяется последним и поэтому побеждает глобальный (человек
            // не поехал в командировку и в это время работает, или у него
            // свой отпуск/рабочий день, отличный от общего). Внутри каждого
            // уровня "рабочий" тоже проверяется после "нерабочего" - так
            // рабочий день побеждает нерабочий на одном и том же уровне.
            //
            // На каждом уровне важно только САМО совпадение (есть ли вообще
            // период, который покрывает этот день), а не то, какой именно
            // из периодов совпал - поэтому everywhere .Any(), а не
            // Single/FirstOrDefault: несколько пересекающихся периодов
            // одного типа (например, гос. праздники и командировка команды
            // в те же даты) - штатный, ожидаемый случай, а не ошибка данных,
            // и не должен ронять солвер.
            var workDay = d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (workDay && globalCalendar?.NonWorkingDays is not null &&
                globalCalendar.NonWorkingDays.Any(x => d >= x.From && d <= x.To))
            {
                workDay = false;
            }

            if (!workDay && globalCalendar?.WorkingDays is not null &&
                globalCalendar.WorkingDays.Any(x => d >= x.From && d <= x.To))
            {
                workDay = true;
            }

            if (workDay && resourceCalendar?.NonWorkingDays is not null &&
                resourceCalendar.NonWorkingDays.Any(x => d >= x.From && d <= x.To))
            {
                workDay = false;
            }

            if (!workDay && resourceCalendar?.WorkingDays is not null &&
                resourceCalendar.WorkingDays.Any(x => d >= x.From && d <= x.To))
            {
                workDay = true;
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