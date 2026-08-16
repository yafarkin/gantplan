using GantPlan.Dtos;
using GantPlan.Logic;

namespace Tests;

public class CalendarLogicTests
{
    // Регрессия: два периода в глобальном NonWorkingDays, пересекающиеся на
    // одну и ту же дату (гос. праздник + командировка команды в те же дни -
    // штатный, ожидаемый случай, не ошибка данных). Раньше global-ветка
    // использовала SingleOrDefault и падала InvalidOperationException'ом
    // ровно на таком, совершенно легитимном календаре.
    [Test]
    public void OverlappingGlobalNonWorkingPeriodsShouldNotThrowTest()
    {
        var globalCalendar = new CalendarDto
        {
            NonWorkingDays =
            [
                new CalendarPeriod { From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 1, 8) }, // гос. праздники
                new CalendarPeriod { From = new DateOnly(2026, 1, 5), To = new DateOnly(2026, 1, 10) } // командировка команды
            ]
        };

        CalendarLogic? calendar = null;
        Assert.DoesNotThrow(() =>
        {
            calendar = new CalendarLogic(30, new DateOnly(2026, 1, 1), globalCalendar, null);
        });

        // день внутри пересечения (05.01-08.01) всё равно должен быть
        // нерабочим - какой именно период "сработал", не важно.
        var overlapDayIndex = new DateOnly(2026, 1, 6).DayNumber - new DateOnly(2026, 1, 1).DayNumber;
        Assert.That(calendar!.NonWorkingDays.Contains(overlapDayIndex), Is.True);
    }

    // На уровне персонального календаря пересечение периодов - это уже
    // ошибка данных (см. ProjectValidator.ValidateResourceCalendars и
    // OverlappingResourceCalendarPeriodsShouldFailValidationTest в
    // TaskAlignmentTests) - её ловят и отклоняют ДО того, как дело доходит
    // до CalendarLogic. Но сам CalendarLogic - чисто механический класс, он
    // не знает о существовании валидатора и не должен падать вообще ни на
    // каких входных периодах (например, если его когда-нибудь вызовут не
    // через обычный путь TaskAlignment -> ProjectValidator -> Solver) -
    // поэтому здесь по-прежнему просто "не падает", без переоценки того,
    // хорошие это данные или плохие.
    [Test]
    public void OverlappingResourceNonWorkingPeriodsShouldNotThrowTest()
    {
        var resourceCalendar = new CalendarDto
        {
            NonWorkingDays =
            [
                new CalendarPeriod { From = new DateOnly(2026, 2, 1), To = new DateOnly(2026, 2, 10) }, // отпуск
                new CalendarPeriod { From = new DateOnly(2026, 2, 5), To = new DateOnly(2026, 2, 7) } // + больничный внутри отпуска
            ]
        };

        CalendarLogic? calendar = null;
        Assert.DoesNotThrow(() =>
        {
            calendar = new CalendarLogic(60, new DateOnly(2026, 1, 1), null, resourceCalendar);
        });

        var overlapDayIndex = new DateOnly(2026, 2, 6).DayNumber - new DateOnly(2026, 1, 1).DayNumber;
        Assert.That(calendar!.NonWorkingDays.Contains(overlapDayIndex), Is.True);
    }

    // Приоритет "локальный важнее глобального": команда в командировке
    // (глобально нерабочий период), но конкретный человек не поехал и в это
    // время работает (у него это явно указано как персональный WorkingDays).
    [Test]
    public void ResourceWorkingDaysShouldOverrideGlobalNonWorkingDaysTest()
    {
        var globalCalendar = new CalendarDto
        {
            NonWorkingDays = [new CalendarPeriod { From = new DateOnly(2026, 3, 2), To = new DateOnly(2026, 3, 6) }] // командировка команды
        };
        var resourceCalendar = new CalendarDto
        {
            WorkingDays = [new CalendarPeriod { From = new DateOnly(2026, 3, 2), To = new DateOnly(2026, 3, 6) }] // этот человек не поехал
        };

        var calendar = new CalendarLogic(30, new DateOnly(2026, 3, 2), globalCalendar, resourceCalendar);

        var dayIndex = new DateOnly(2026, 3, 4).DayNumber - new DateOnly(2026, 3, 2).DayNumber;
        Assert.That(calendar.NonWorkingDays.Contains(dayIndex), Is.False, "локальный WorkingDays должен победить глобальный NonWorkingDays");
    }

    // Приоритет "рабочий важнее нерабочего" на одном и том же (глобальном)
    // уровне: суббота, но глобально объявлена рабочей (отработка).
    [Test]
    public void GlobalWorkingDaysShouldOverrideWeekendTest()
    {
        var saturday = new DateOnly(2026, 1, 3);
        Assert.That(saturday.DayOfWeek, Is.EqualTo(DayOfWeek.Saturday));

        var globalCalendar = new CalendarDto
        {
            WorkingDays = [new CalendarPeriod { From = saturday, To = saturday }]
        };

        var calendar = new CalendarLogic(10, new DateOnly(2026, 1, 1), globalCalendar, null);

        var dayIndex = saturday.DayNumber - new DateOnly(2026, 1, 1).DayNumber;
        Assert.That(calendar.NonWorkingDays.Contains(dayIndex), Is.False);
    }
}
