using System.Text;

namespace GantPlan.Visualize;

// Строка отчёта по одному человеку: сколько рабочих дней доступно и как они
// разошлись по бизнесу, остальным задачам и резерву на влёты.
internal sealed record CapacityRow(
    string Name, string Role, int Available, int Business, int NonBusiness, QuarterCapacity Allocation)
{
    // Дни, на которые задач не назначено вовсе: из них и набирается резерв.
    public int Unassigned => Available - Business - NonBusiness;

    // Сколько дней резерва реально осталось пустыми. Если план залез в резерв,
    // недостающая часть и есть Overload, поэтому вычитаем именно его.
    public int CoveredReserve => Allocation.Quota - Allocation.Overload;
}

// Общий для консоли и daily_plan.txt вывод: таблица по людям, разбор тех, у
// кого план съел резерв, и одна итоговая сводка.
internal static class CapacityReport
{
    private const string TotalName = "ВСЕГО";
    private const string SubtotalName = "ИТОГО";
    private const string AllRoles = "все";

    private const int DaysWidth = 4;
    private const int ReserveWidth = 16;
    private const int TaskWidth = 9;

    // Роль в данных может быть не заполнена, а колонка должна остаться ровной.
    public static string NormalizeRole(string? role) =>
        string.IsNullOrWhiteSpace(role) ? "—" : role;

    public static string Render(IReadOnlyList<CapacityRow> rows, double reserveShare)
    {
        // Роль берём из данных, поэтому отчёт ничего не требует настраивать:
        // люди идут сгруппированно по ролям, а подытог по роли появляется
        // сам, как только ролей в выборке больше одной.
        var people = rows.Where(r => r.Available > 0)
            .OrderBy(r => r.Role)
            .ThenBy(r => r.Name)
            .ToList();
        var absent = rows.Where(r => r.Available == 0).ToList();
        var byRole = people.GroupBy(r => r.Role).ToList();
        var total = Total(people, TotalName, AllRoles);

        var sb = new StringBuilder();
        sb.AppendLine($"Ёмкость = рабочие дни минус резерв на влёты ({reserveShare * 100:0}%). " +
                      "Проценты в таблице — от ёмкости.");
        sb.AppendLine("Резерв: цель — сколько дней хотели оставить под влёты, " +
                      "факт — сколько осталось пустыми.");
        sb.AppendLine();

        var nameWidth = Math.Max(TotalName.Length, people.Select(p => p.Name.Length).Append(3).Max());
        var roleWidth = Math.Max(AllRoles.Length, people.Select(p => p.Role.Length).Append(4).Max());
        var ruler = Ruler(nameWidth, roleWidth);

        sb.AppendLine(Join(
            "Имя".PadRight(nameWidth),
            "Роль".PadRight(roleWidth),
            "Дни".PadLeft(DaysWidth),
            "Резерв цель/факт",
            Center("Бизнес", TaskWidth),
            Center("Не-бизнес", TaskWidth),
            Center("Свободно", TaskWidth)));
        sb.AppendLine(ruler);

        foreach (var person in people)
        {
            sb.AppendLine(Row(person, nameWidth, roleWidth));
        }

        sb.AppendLine(ruler);
        if (byRole.Count > 1)
        {
            foreach (var role in byRole)
            {
                sb.AppendLine(Row(Total(role, SubtotalName, role.Key), nameWidth, roleWidth));
            }
        }

        sb.AppendLine(Row(total, nameWidth, roleWidth));

        if (absent.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Без доступных дней в периоде: " +
                          $"{string.Join(", ", absent.Select(r => $"{r.Name} ({r.Role})"))}.");
        }

        AppendAttention(sb, people, total);
        AppendSummary(sb, total);

        return sb.ToString();
    }

    // Итоговая строка. Квота считается суммой округлённых персональных квот, а
    // не округлением от суммы: иначе итог не сходился бы со строками людей.
    private static CapacityRow Total(IEnumerable<CapacityRow> source, string name, string role)
    {
        var rows = source.ToList();
        var available = rows.Sum(r => r.Available);
        var quota = rows.Sum(r => r.Allocation.Quota);
        var allocation = new QuarterCapacity(
            quota, available - quota, rows.Sum(r => r.Allocation.Idle), rows.Sum(r => r.Allocation.Overload));

        return new CapacityRow(
            name, role, available, rows.Sum(r => r.Business), rows.Sum(r => r.NonBusiness), allocation);
    }

    private static string Row(CapacityRow row, int nameWidth, int roleWidth)
    {
        var status = row.Allocation.Overload > 0 ? $"-{row.Allocation.Overload}" : "ok";
        var capacity = row.Allocation.Capacity;

        return Join(
            row.Name.PadRight(nameWidth),
            row.Role.PadRight(roleWidth),
            row.Available.ToString().PadLeft(DaysWidth),
            $"{row.Allocation.Quota,4} /{row.CoveredReserve,4}  {status,-4}",
            Cell(row.Business, capacity),
            Cell(row.NonBusiness, capacity),
            Cell(row.Allocation.Idle, capacity));
    }

    private static void AppendAttention(StringBuilder sb, IReadOnlyList<CapacityRow> people, CapacityRow total)
    {
        var shortfall = people.Where(p => p.Allocation.Overload > 0).ToList();
        if (shortfall.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("Требуют внимания:");
        foreach (var person in shortfall)
        {
            var planned = person.Business + person.NonBusiness;
            sb.AppendLine();
            sb.AppendLine($"  {person.Name}: рабочих дней {person.Available} = " +
                          $"{person.Allocation.Quota} резерв + {person.Allocation.Capacity} ёмкость.");
            sb.AppendLine($"    Задачами занято {planned} {Days(planned)} при ёмкости " +
                          $"{person.Allocation.Capacity}, перебор {person.Allocation.Overload} " +
                          $"{Days(person.Allocation.Overload)}.");
            sb.AppendLine($"    Свободными осталось {person.CoveredReserve} " +
                          $"{Days(person.CoveredReserve)} вместо {person.Allocation.Quota} — " +
                          $"резерв закрыт на {Percent(person.CoveredReserve, person.Allocation.Quota)}.");
        }

        sb.AppendLine();
        sb.AppendLine(total.Unassigned >= total.Allocation.Quota
            ? $"  Запас по команде есть: {total.Unassigned} свободных {Days(total.Unassigned)} " +
              $"при цели {total.Allocation.Quota}, но он не у тех людей."
            : $"  Запаса по команде нет: свободно {total.Unassigned} {Days(total.Unassigned)} " +
              $"при цели {total.Allocation.Quota}.");

        var spare = people.Where(p => p.Allocation.Idle > 0).ToList();
        if (spare.Count > 0)
        {
            sb.AppendLine($"  Свободно сверх резерва: {List(spare, p => p.Allocation.Idle)}.");
        }

        sb.AppendLine($"  Не хватает резерва: {List(shortfall, p => p.Allocation.Overload)}.");
    }

    private static void AppendSummary(StringBuilder sb, CapacityRow total)
    {
        var capacity = total.Allocation.Capacity;
        sb.AppendLine();
        sb.AppendLine($"Итого: бизнес {total.Business} из {capacity} дней ёмкости " +
                      $"({Percent(total.Business, capacity)}), это " +
                      $"{Percent(total.Business, total.Available)} от всех {total.Available} рабочих дней.");
        sb.AppendLine($"       не-бизнес {total.NonBusiness} ({Percent(total.NonBusiness, capacity)}), " +
                      $"свободно сверх резерва {total.Allocation.Idle} " +
                      $"({Percent(total.Allocation.Idle, capacity)}).");
        sb.AppendLine($"Резерв: цель {total.Allocation.Quota} {Days(total.Allocation.Quota)}, " +
                      $"обеспечено {total.CoveredReserve}.");
    }

    private static string List(IEnumerable<CapacityRow> rows, Func<CapacityRow, int> value) =>
        string.Join(", ", rows.OrderByDescending(value).Select(r => $"{r.Name} {value(r)}"));

    private static string Join(params string[] cells) => string.Join(" │ ", cells);

    private static string Ruler(int nameWidth, int roleWidth) => string.Join("─┼─",
        new string('─', nameWidth), new string('─', roleWidth), new string('─', DaysWidth),
        new string('─', ReserveWidth), new string('─', TaskWidth), new string('─', TaskWidth),
        new string('─', TaskWidth));

    private static string Cell(int value, int capacity) => $"{value,4} {Percent(value, capacity),4}";

    private static string Center(string text, int width) =>
        text.PadLeft(text.Length + (width - text.Length) / 2).PadRight(width);

    private static string Percent(int value, int total) =>
        total > 0 ? $"{(int)Math.Round(100.0 * value / total, MidpointRounding.AwayFromZero)}%" : "0%";

    // «1 день», «2 дня», «5 дней» — иначе строки разбора читаются коряво.
    private static string Days(int count)
    {
        var tens = count % 100;
        if (tens is >= 11 and <= 14)
        {
            return "дней";
        }

        return (count % 10) switch
        {
            1 => "день",
            2 or 3 or 4 => "дня",
            _ => "дней"
        };
    }
}
