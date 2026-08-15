using GantPlan.Dtos;

namespace GantPlan.Logic;

// Строит по дереву задач служебные индексы по id, ничего не копируя и не
// проверяя бизнес-правила - только структуру дерева (непустой/уникальный id).
// Ключевая идея: для листа и для группы задач индекс работает одинаково -
// IdToLeafIds[id] всегда возвращает id листьев внутри этого узла (для листа -
// его самого). Это позволяет резолвить "задача зависит от группы задач" так
// же просто, как "задача зависит от одной задачи" - без отдельной ветки кода
// на случай группы.
public sealed class TaskTreeIndex
{
    // Все узлы дерева (и группы, и листья) - используется, чтобы дописать
    // результат решения обратно в объекты, которые передал вызывающий код.
    public IReadOnlyDictionary<string, TaskDto> AllTasks { get; }

    // Только листовые задачи - то, что реально идёт в решение.
    public IReadOnlyDictionary<string, TaskDto> Leaves { get; }

    private readonly Dictionary<string, IReadOnlyList<string>> _idToLeafIds;

    private TaskTreeIndex(
        Dictionary<string, TaskDto> allTasks,
        Dictionary<string, TaskDto> leaves,
        Dictionary<string, IReadOnlyList<string>> idToLeafIds)
    {
        AllTasks = allTasks;
        Leaves = leaves;
        _idToLeafIds = idToLeafIds;
    }

    public static TaskTreeIndex Build(TaskDto root)
    {
        var allTasks = new Dictionary<string, TaskDto>();
        var leaves = new Dictionary<string, TaskDto>();
        var idToLeafIds = new Dictionary<string, IReadOnlyList<string>>();

        CollectLeafIds(root, allTasks, leaves, idToLeafIds);

        return new TaskTreeIndex(allTasks, leaves, idToLeafIds);
    }

    public bool Contains(string id) => _idToLeafIds.ContainsKey(id);

    /// <summary>
    /// Разворачивает список id (без разницы - лист это или группа задач) в
    /// плоский список id листьев. Для группы это id вообще всех задач внутри
    /// неё - именно так теперь работает "задача зависит от группы задач":
    /// она стартует только когда завершены все листья внутри группы.
    /// </summary>
    public List<string> ExpandToLeafIds(IEnumerable<string> ids)
    {
        var result = new List<string>();

        foreach (var id in ids)
        {
            if (!_idToLeafIds.TryGetValue(id, out var leafIds))
            {
                throw new Exception($"Task with id {id} not found");
            }

            result.AddRange(leafIds);
        }

        return result;
    }

    // Обходит поддерево снизу вверх и возвращает id всех листьев внутри него;
    // попутно регистрирует узел в allTasks/leaves и кладёт результат в
    // idToLeafIds - под своим собственным id, независимо от того, лист это
    // или группа.
    private static IReadOnlyList<string> CollectLeafIds(
        TaskDto task,
        Dictionary<string, TaskDto> allTasks,
        Dictionary<string, TaskDto> leaves,
        Dictionary<string, IReadOnlyList<string>> idToLeafIds)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            throw new Exception("Task must have an id");
        }

        if (!allTasks.TryAdd(task.Id, task))
        {
            throw new Exception($"Task with id {task.Id} already exists");
        }

        List<string> leafIds;

        if (task.HasChild)
        {
            leafIds = new List<string>();
            foreach (var child in task.Children)
            {
                leafIds.AddRange(CollectLeafIds(child, allTasks, leaves, idToLeafIds));
            }
        }
        else
        {
            leaves.Add(task.Id, task);
            leafIds = [task.Id];
        }

        idToLeafIds[task.Id] = leafIds;

        return leafIds;
    }
}
