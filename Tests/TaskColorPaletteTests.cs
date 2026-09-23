using GantPlan.Dtos;
using GantPlan.Visualize;

namespace Tests;

// Проверяем ровно то, ради чего палитра переделывалась: цвет на эпик (а не
// на верхний уровень дерева), оттенки внутри эпика и устойчивость цветов к
// перегенерации плана.
public sealed class TaskColorPaletteTests
{
    // Дерево вида корень -> направление -> эпик (с JiraKey) -> задачи:
    // именно на такой форме старая раскраска "по детям корня" давала всего
    // два цвета на весь план.
    private static TaskDto BuildTree(params string[] epicKeys)
    {
        var epics = epicKeys
            .Select((key, i) => new TaskDto
            {
                Id = $"1.D.{i}",
                JiraKey = key,
                Name = key,
                Children =
                [
                    new TaskDto {Id = $"1.D.{i}.1", JiraKey = $"{key}-A", Name = "анализ"},
                    new TaskDto {Id = $"1.D.{i}.2", JiraKey = $"{key}-B", Name = "разработка"},
                    new TaskDto {Id = $"1.D.{i}.3", Name = "тестирование"}
                ]
            })
            .ToList();

        return new TaskDto
        {
            Id = "1",
            Name = "Проект",
            Children = [new TaskDto {Id = "1.D", Name = "Направление", Children = epics}]
        };
    }

    private static TaskDto Find(TaskDto root, string id) =>
        root.Id == id
            ? root
            : root.Children.Select(c => TryFind(c, id)).FirstOrDefault(t => t is not null)
              ?? throw new InvalidOperationException($"нет задачи {id}");

    private static TaskDto? TryFind(TaskDto root, string id) =>
        root.Id == id ? root : root.Children.Select(c => TryFind(c, id)).FirstOrDefault(t => t is not null);

    private static double Hue(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta < 1e-9)
        {
            return 0;
        }

        var hue = max == r ? 60 * ((g - b) / delta % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);

        return (hue + 360) % 360;
    }

    // угловое расстояние по цветовому кругу
    private static double HueDistance(double a, double b) => Math.Min(Math.Abs(a - b), 360 - Math.Abs(a - b));

    [Test]
    public void EachEpicGetsItsOwnColor()
    {
        var epicKeys = Enumerable.Range(1, 16).Select(i => $"PRJ-{i * 100}").ToArray();
        var root = BuildTree(epicKeys);

        var colors = TaskColorPalette.Build(root);
        var epicColors = Enumerable.Range(0, epicKeys.Length)
            .Select(i => colors[Find(root, $"1.D.{i}")])
            .ToList();

        Assert.That(epicColors.Distinct().Count(), Is.EqualTo(epicKeys.Length), "цвета эпиков должны быть разными");
        Assert.That(epicColors.Select(Hue).Distinct().Count(), Is.EqualTo(epicKeys.Length),
            "эпики должны отличаться тоном, а не только яркостью");
    }

    [Test]
    public void TasksOfEpicKeepEpicHueButDifferInShade()
    {
        var root = BuildTree("PRJ-100", "PRJ-200");
        var colors = TaskColorPalette.Build(root);

        var epicHue = Hue(colors[Find(root, "1.D.0")]);
        var taskColors = new[] {"1.D.0.1", "1.D.0.2", "1.D.0.3"}.Select(id => colors[Find(root, id)]).ToList();

        Assert.That(taskColors.Distinct().Count(), Is.EqualTo(3), "оттенки задач внутри эпика должны различаться");
        foreach (var color in taskColors)
        {
            Assert.That(HueDistance(Hue(color), epicHue), Is.LessThanOrEqualTo(16),
                $"оттенок задачи {color} должен остаться в рамках цвета эпика");
        }

        // и при этом не совпасть с соседним эпиком
        var otherEpicHue = Hue(colors[Find(root, "1.D.1")]);
        Assert.That(HueDistance(epicHue, otherEpicHue), Is.GreaterThan(20));
    }

    [Test]
    public void ColorOfEpicSurvivesReplanning()
    {
        var beforeRoot = BuildTree("PRJ-100", "PRJ-200", "PRJ-300");
        var beforeColors = TaskColorPalette.Build(beforeRoot);

        // тот же эпик, но дерево перегенерировано: другой порядок эпиков,
        // другие Id, у одного эпика добавилась задача
        var afterRoot = BuildTree("PRJ-300", "PRJ-100");
        var afterColors = TaskColorPalette.Build(afterRoot);

        Assert.That(afterColors[Find(afterRoot, "1.D.1")], Is.EqualTo(beforeColors[Find(beforeRoot, "1.D.0")]),
            "PRJ-100 должен остаться того же цвета");
        Assert.That(afterColors[Find(afterRoot, "1.D.0")], Is.EqualTo(beforeColors[Find(beforeRoot, "1.D.2")]),
            "PRJ-300 должен остаться того же цвета");
    }

    [Test]
    public void TaskShadeSurvivesReplanning()
    {
        var beforeRoot = BuildTree("PRJ-100");
        var afterRoot = BuildTree("PRJ-100");
        // перегенерация переставила детей эпика местами
        var afterEpic = Find(afterRoot, "1.D.0");
        var reordered = afterEpic.Children.Reverse().ToList();
        afterEpic.Children.Clear();
        foreach (var child in reordered)
        {
            afterEpic.Children.Add(child);
        }

        var beforeColors = TaskColorPalette.Build(beforeRoot);
        var afterColors = TaskColorPalette.Build(afterRoot);

        foreach (var id in new[] {"1.D.0.1", "1.D.0.2", "1.D.0.3"})
        {
            Assert.That(afterColors[Find(afterRoot, id)], Is.EqualTo(beforeColors[Find(beforeRoot, id)]), id);
        }
    }

    [Test]
    public void ContainersAboveEpicsAreNeutral()
    {
        var root = BuildTree("PRJ-100", "PRJ-200");
        var colors = TaskColorPalette.Build(root);

        Assert.That(colors[root], Is.EqualTo(TaskColorPalette.StructuralColor));
        Assert.That(colors[Find(root, "1.D.0")], Is.Not.EqualTo(TaskColorPalette.StructuralColor));
    }

    [Test]
    public void TreeWithoutJiraKeysIsColoredByTopLevelGroups()
    {
        var root = new TaskDto
        {
            Id = "1",
            Name = "Проект",
            Children =
            [
                new TaskDto {Id = "1.1", Name = "Группа 1", Children = [new TaskDto {Id = "1.1.1", Name = "Задача"}]},
                new TaskDto {Id = "1.2", Name = "Группа 2", Children = [new TaskDto {Id = "1.2.1", Name = "Задача"}]}
            ]
        };

        var colors = TaskColorPalette.Build(root);

        Assert.That(Hue(colors[Find(root, "1.1")]), Is.Not.EqualTo(Hue(colors[Find(root, "1.2")])));
        Assert.That(HueDistance(Hue(colors[Find(root, "1.1")]), Hue(colors[Find(root, "1.1.1")])),
            Is.LessThanOrEqualTo(16));
    }

    // Контракт между бэкендом и шаблоном: бар красится значением node.Color,
    // а прежнего ColorGroupId/colorScale в шаблоне больше нет (иначе цвета
    // молча вернулись бы к "десяти цветам d3 по детям корня").
    [Test]
    public void GeneratedHtmlColorsBarsByNodeColor()
    {
        var root = BuildTree("PRJ-100", "PRJ-200");
        var project = new ProjectDto {ProjectStart = new DateOnly(2026, 1, 12), RootTask = root};
        var template = Path.Combine(TestContext.CurrentContext.TestDirectory, "Templates", "task_tree_template.html");
        var output = Path.Combine(Path.GetTempPath(), $"timeline-{Guid.NewGuid():N}.html");

        try
        {
            TaskTreeDataPreparer.GenerateHtml(template, output, TaskTreeDataPreparer.Prepare(project));
            var html = File.ReadAllText(output);

            Assert.That(html, Does.Not.Contain("ColorGroupId"));
            Assert.That(html, Does.Contain("node.Color"));
            foreach (var color in TaskColorPalette.Build(root).Values.Distinct())
            {
                Assert.That(html, Does.Contain($"\"Color\":\"{color}\""), color);
            }
        }
        finally
        {
            File.Delete(output);
        }
    }

    // Эпиков обычно не больше 16 (по числу тонов палитры), но если их
    // больше - цвета всё равно должны остаться разными: палитра идёт на
    // второй круг тех же тонов с другой светлотой.
    [Test]
    public void MoreEpicsThanHuesStillGetDistinctColors()
    {
        var root = BuildTree(Enumerable.Range(1, 30).Select(i => $"PRJ-{i * 11}").ToArray());

        var colors = TaskColorPalette.Build(root);
        var epicColors = Enumerable.Range(0, 30).Select(i => colors[Find(root, $"1.D.{i}")]).ToList();

        Assert.That(epicColors.Distinct().Count(), Is.EqualTo(30));
    }
}
