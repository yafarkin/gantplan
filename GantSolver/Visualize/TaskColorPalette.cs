using System.Runtime.CompilerServices;
using System.Text;
using GantPlan.Dtos;

namespace GantPlan.Visualize;

/// <summary>
///     Раскраска баров в timeline.html: один эпик - один собственный цвет,
///     задачи внутри эпика - оттенки этого же цвета.
///     <para>
///         Раньше цвет брался из d3.schemeCategory10 по id ближайшего предка на
///         глубине 1, поэтому на реальном дереве (корень -> пара крупных
///         направлений -> эпики -> задачи) весь план получался двух-трёх
///         цветов: цветовых групп было ровно столько, сколько детей у корня.
///     </para>
///     <para>
///         Теперь цветовая группа - это "входной эпик": первый сверху узел с
///         собственным JiraKey. Всё его поддерево красится вариациями его
///         цвета, так что по цвету бара видно, к какому эпику относится
///         задача, а по оттенку - что задачи разные.
///     </para>
///     <para>
///         Цвет эпика выводится из хэша его ключа (JiraKey, а если ключа нет -
///         Id), а не из порядка обхода дерева: после перегенерации плана
///         (другой порядок задач, новые/убранные задачи, другие даты) эпик
///         сохраняет тот же цвет. Хэш - FNV-1a, а не string.GetHashCode:
///         последний рандомизируется от запуска к запуску, и цвета "прыгали"
///         бы при каждой генерации.
///     </para>
/// </summary>
public static class TaskColorPalette
{
    /// <summary>
    ///     Цвет узлов выше уровня эпиков (корень и структурные группировки над
    ///     эпиками): нейтральный серый - чтобы "цвет = эпик" читалось
    ///     однозначно и контейнеры не съедали цвета палитры. Достаточно тёмный,
    ///     чтобы белая подпись внутри бара оставалась читаемой.
    /// </summary>
    public const string StructuralColor = "#868e96";

    /// <summary>
    ///     Базовых цветов (тонов) в палитре. Эпиков, как правило, не больше
    ///     16, поэтому каждому достаётся свой тон; если эпиков больше -
    ///     добавляется ещё один "круг" тех же тонов с другой светлотой
    ///     (см. LapShift).
    /// </summary>
    public const int HueCount = 16;

    // Минимальный контраст цвета бара с белой подписью внутри него
    // (см. EnsureLabelContrast). 2.6 - примерно на уровне самых светлых
    // цветов прежней палитры d3.schemeCategory10, но теперь это гарантия
    // для всех оттенков, а не случайность.
    private const double MinLabelContrast = 2.6;

    // Тона по кругу, но в порядке "разворота битов" (0, 8, 4, 12, 2, ...):
    // соседние слоты палитры оказываются максимально далеко друг от друга по
    // цветовому кругу, поэтому при 2-3 эпиках цвета получаются контрастными,
    // а не тремя оттенками красного.
    private static readonly double[] Hues =
    [
        0, 180, 90, 270, 45, 225, 135, 315, 22.5, 202.5, 112.5, 292.5, 67.5, 247.5, 157.5, 337.5
    ];

    // Сдвиги для второго и последующих "кругов" палитры (нужны только если
    // эпиков больше 16): тот же тон, но заметно светлее/темнее.
    private static readonly (double L, double S)[] LapShift = [(0, 0), (12, -10), (-10, 8)];

    // Лесенка оттенков внутри эпика: индекс 0 - сам эпик (базовый цвет),
    // 1..N - его задачи. Меняем в основном светлоту, тон и насыщенность -
    // чуть-чуть, чтобы оттенок читался как "тот же цвет", а не как другой
    // эпик.
    private static readonly (double L, double H, double S)[] Shades =
    [
        (0, 0, 0),
        (11, -7, 6),
        (-9, 7, -6),
        (19, -4, -10),
        (-15, 4, 8),
        (6, 10, -4),
        (15, -10, 4),
        (-5, 14, 10),
        (22, 5, -8),
        (-12, -5, 6),
        (8, 12, -6),
        (-3, -12, 0)
    ];

    /// <summary>
    ///     Считает цвет (hex) для каждой задачи дерева. Ключ словаря
    ///     сравнивается по ссылке: одинаковые по содержимому задачи в разных
    ///     ветках - это разные узлы с разными цветами.
    /// </summary>
    public static IReadOnlyDictionary<TaskDto, string> Build(TaskDto root)
    {
        var hasJiraInSubtree = new HashSet<TaskDto>(ReferenceComparer.Instance);
        MarkSubtreesWithJira(root, hasJiraInSubtree);

        // узел, с которого начинается эпик, и остальные узлы этого эпика
        var epicRoots = new Dictionary<string, TaskDto>(StringComparer.Ordinal);
        var epicMembers = new Dictionary<string, List<TaskDto>>(StringComparer.Ordinal);
        var structural = new List<TaskDto>();

        CollectEpics(root, null, 0, hasJiraInSubtree, epicRoots, epicMembers, structural);

        var slots = AssignSlots(
            epicRoots.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            Math.Max(1, (epicRoots.Count + HueCount - 1) / HueCount) * HueCount);

        var colors = new Dictionary<TaskDto, string>(ReferenceComparer.Instance);
        foreach (var task in structural)
        {
            colors[task] = StructuralColor;
        }

        foreach (var (epicKey, epicRoot) in epicRoots)
        {
            var baseColor = BaseColor(slots[epicKey]);
            colors[epicRoot] = ToHex(baseColor);

            if (!epicMembers.TryGetValue(epicKey, out var members))
            {
                continue;
            }

            // оттенок задачи - тоже от её собственного хэша (а не от номера
            // по порядку), чтобы перестановка задач внутри эпика не
            // перекрашивала их; коллизии разводим детерминированно - по
            // отсортированному списку ключей, а не по порядку в дереве.
            var shades = AssignShades(members);
            foreach (var member in members)
            {
                colors[member] = ToHex(Shade(baseColor, Shades[shades[member]]));
            }
        }

        return colors;
    }

    /// <summary>
    ///     Ключ задачи для хэширования: JiraKey (не меняется при
    ///     перенумерации дерева), иначе Id.
    /// </summary>
    public static string TaskKey(TaskDto task) =>
        !string.IsNullOrWhiteSpace(task.JiraKey) ? "jira:" + task.JiraKey.Trim() : "id:" + task.Id;

    private static bool MarkSubtreesWithJira(TaskDto task, HashSet<TaskDto> result)
    {
        var hasJira = !string.IsNullOrWhiteSpace(task.JiraKey);
        foreach (var child in task.Children)
        {
            hasJira |= MarkSubtreesWithJira(child, result);
        }

        if (hasJira)
        {
            result.Add(task);
        }

        return hasJira;
    }

    // Определяет "входной эпик" для каждого узла:
    //   * первый сверху (начиная с глубины 1) узел с собственным JiraKey -
    //     это эпик, всё его поддерево принадлежит ему;
    //   * если в поддереве узла нет ни одного JiraKey - эпиком считается сам
    //     этот узел (так план вообще без Jira-ключей красится по крупным
    //     группам, как раньше, а не одним серым цветом);
    //   * всё, что выше эпиков (корень, промежуточные группировки), -
    //     структурные узлы без собственного цвета.
    private static void CollectEpics(
        TaskDto task,
        string? epicKey,
        int depth,
        HashSet<TaskDto> hasJiraInSubtree,
        Dictionary<string, TaskDto> epicRoots,
        Dictionary<string, List<TaskDto>> epicMembers,
        List<TaskDto> structural)
    {
        var isEpicRoot = false;
        if (epicKey is null && depth >= 1)
        {
            if (!string.IsNullOrWhiteSpace(task.JiraKey) || !hasJiraInSubtree.Contains(task))
            {
                epicKey = TaskKey(task);
                isEpicRoot = true;
            }
        }

        if (epicKey is null)
        {
            structural.Add(task);
        }
        else if (isEpicRoot && !epicRoots.ContainsKey(epicKey))
        {
            epicRoots[epicKey] = task;
        }
        else
        {
            // сюда же попадает узел с дублирующимся ключом эпика: цвет
            // эпика уже занят первым владельцем, этот станет его оттенком
            if (!epicMembers.TryGetValue(epicKey, out var members))
            {
                members = [];
                epicMembers[epicKey] = members;
            }

            members.Add(task);
        }

        foreach (var child in task.Children)
        {
            CollectEpics(child, epicKey, depth + 1, hasJiraInSubtree, epicRoots, epicMembers, structural);
        }
    }

    // Слот палитры = хэш ключа по модулю числа слотов; занятый слот -
    // ближайший свободный дальше по кругу. Ключи разбираются в
    // отсортированном порядке, поэтому результат не зависит от порядка
    // обхода дерева.
    private static Dictionary<string, int> AssignSlots(IReadOnlyList<string> keys, int slotCount)
    {
        var used = new HashSet<int>();
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var key in keys)
        {
            var start = (int) (Hash(key) % (uint) slotCount);
            var slot = start;
            for (var i = 0; i < slotCount; i++)
            {
                var candidate = (start + i) % slotCount;
                if (used.Add(candidate))
                {
                    slot = candidate;
                    break;
                }
            }

            result[key] = slot;
        }

        return result;
    }

    // То же самое, но по лесенке оттенков внутри одного эпика (индекс 0
    // занят самим эпиком). Задач в эпике может быть больше, чем оттенков в
    // лесенке - тогда оттенки начинают повторяться, это нормально: цвет
    // эпика всё равно узнаётся, а различать задачи внутри него помогают
    // подписи.
    private static Dictionary<TaskDto, int> AssignShades(IReadOnlyList<TaskDto> tasks)
    {
        var shadeCount = Shades.Length - 1;
        var used = new HashSet<int>();
        var result = new Dictionary<TaskDto, int>(ReferenceComparer.Instance);

        foreach (var task in tasks.OrderBy(TaskKey, StringComparer.Ordinal))
        {
            var start = (int) (Hash(TaskKey(task)) % (uint) shadeCount);
            var shade = start;
            for (var i = 0; i < shadeCount; i++)
            {
                var candidate = (start + i) % shadeCount;
                if (used.Add(candidate))
                {
                    shade = candidate;
                    break;
                }
            }

            result[task] = shade + 1;
        }

        return result;
    }

    private static (double H, double S, double L) BaseColor(int slot)
    {
        var hue = Hues[slot % HueCount];
        var (lapL, lapS) = LapShift[slot / HueCount % LapShift.Length];

        // жёлто-зелёные тона глазом выглядят светлее синих при одинаковой
        // светлоте HSL - подгоняем L по кругу, чтобы цвета были примерно
        // одной "тяжести" и белая подпись читалась на всех
        var perceptual = -6 * Math.Cos((hue - 60) * Math.PI / 180);

        return (hue, Clamp(58 + lapS, 25, 85), Clamp(44 + perceptual + lapL, 26, 60));
    }

    private static (double H, double S, double L) Shade((double H, double S, double L) baseColor,
        (double L, double H, double S) shift) =>
        ((baseColor.H + shift.H + 360) % 360,
            Clamp(baseColor.S + shift.S, 25, 85),
            Clamp(baseColor.L + shift.L, 26, 60));

    private static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    private static string ToHex((double H, double S, double L) hsl)
    {
        var (r, g, b) = ToRgb(EnsureLabelContrast(hsl));

        return "#" + Hex(r) + Hex(g) + Hex(b);

        string Hex(double v) => ((int) Math.Round(v * 255)).ToString("x2");
    }

    // Подпись задачи рисуется белым прямо внутри бара, а светлота у HSL
    // "неравномерная" на глаз: голубой/зелёный при L=60 выглядят намного
    // светлее синего при той же L, и белый текст на них тонет. Поэтому
    // после всех сдвигов цвет затемняется, пока контраст с белым не станет
    // приемлемым - так ни один оттенок лесенки не окажется "невычитаемым".
    private static (double H, double S, double L) EnsureLabelContrast((double H, double S, double L) color)
    {
        while (color.L > 22 && ContrastWithWhite(color) < MinLabelContrast)
        {
            color.L -= 1;
        }

        return color;
    }

    private static double ContrastWithWhite((double H, double S, double L) color)
    {
        var (r, g, b) = ToRgb(color);
        var luminance = 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

        return 1.05 / (luminance + 0.05);

        static double Linear(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static (double R, double G, double B) ToRgb((double H, double S, double L) hsl)
    {
        var (h, s, l) = (hsl.H / 360, hsl.S / 100, hsl.L / 100);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        return (Channel(h + 1.0 / 3), Channel(h), Channel(h - 1.0 / 3));

        double Channel(double t)
        {
            t = (t + 1) % 1;

            return t < 1.0 / 6 ? p + (q - p) * 6 * t
                : t < 1.0 / 2 ? q
                : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
                : p;
        }
    }

    // FNV-1a: короткий, стабильный между запусками и платформами хэш -
    // именно этого не даёт string.GetHashCode (рандомизируется на каждый
    // процесс, из-за чего цвета менялись бы при каждой генерации).
    private static uint Hash(string value)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var b in Encoding.UTF8.GetBytes(value))
            {
                hash ^= b;
                hash *= 16777619;
            }

            return hash;
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<TaskDto>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(TaskDto? x, TaskDto? y) => ReferenceEquals(x, y);

        public int GetHashCode(TaskDto obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
