using GantPlan.Dtos;

namespace GantPlan.Logic;

// Проверяет всё, от чего реально зависит корректная работа Solver'а, не
// строя ни одной копии задачи: наличие Limit у листьев, известность
// приоритета, разрешимость исполнителя (только для задач, которые пойдут в
// решение - у отключённых/завершённых он не нужен), существование id
// предшественников и отсутствие циклов между ними. В отличие от старого
// кода, который падал на первой же найденной проблеме, здесь все проблемы
// собираются в один список - чтобы не чинить входные данные по одной ошибке
// за прогон.
public static class ProjectValidator
{
    public static List<string> Validate(
        TaskTreeIndex treeIndex,
        IReadOnlyDictionary<int, long> weights,
        IEnumerable<ResourceDto> resources)
    {
        var errors = new List<string>();
        var resourceList = resources as IReadOnlyCollection<ResourceDto> ?? resources.ToList();

        foreach (var task in treeIndex.Leaves.Values)
        {
            ValidateLeaf(task, weights, resourceList, treeIndex, errors);
        }

        if (treeIndex.Leaves.Values.All(t => t.CanSkipTask))
        {
            errors.Add("No tasks found");
        }

        ValidateNoCycles(treeIndex, errors);
        ValidateResourceCalendars(resourceList, errors);

        return errors;
    }

    // У одного человека периоды внутри одного и того же списка
    // (NonWorkingDays или WorkingDays) не должны пересекаться - в отличие от
    // глобального календаря, где пересечение независимых источников (гос.
    // праздники + командировка команды) - штатный случай, у одного ресурса
    // пересечение почти наверняка значит, что в данные закралась ошибка
    // (дубль записи, опечатка в дате). Пересечение МЕЖДУ NonWorkingDays и
    // WorkingDays одного человека - это не ошибка, а штатный способ
    // переопределить для себя чужое правило (см. CalendarLogic), поэтому
    // списки проверяются по отдельности, не друг против друга.
    private static void ValidateResourceCalendars(IReadOnlyCollection<ResourceDto> resources, List<string> errors)
    {
        foreach (var resource in resources)
        {
            if (resource.Calendar is null)
            {
                continue;
            }

            ValidateNoOverlaps(resource.Name, "NonWorkingDays", resource.Calendar.NonWorkingDays, errors);
            ValidateNoOverlaps(resource.Name, "WorkingDays", resource.Calendar.WorkingDays, errors);
        }
    }

    private static void ValidateNoOverlaps(
        string resourceName, string listName, ICollection<CalendarPeriod>? periods, List<string> errors)
    {
        if (periods is null || periods.Count < 2)
        {
            return;
        }

        var ordered = periods.ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            for (var j = i + 1; j < ordered.Count; j++)
            {
                var a = ordered[i];
                var b = ordered[j];
                if (a.From <= b.To && b.From <= a.To)
                {
                    errors.Add(
                        $"Resource {resourceName} has overlapping {listName} periods: " +
                        $"{a.From:yyyy-MM-dd}..{a.To:yyyy-MM-dd} and {b.From:yyyy-MM-dd}..{b.To:yyyy-MM-dd}");
                }
            }
        }
    }

    private static void ValidateLeaf(
        TaskDto task,
        IReadOnlyDictionary<int, long> weights,
        IReadOnlyCollection<ResourceDto> resources,
        TaskTreeIndex treeIndex,
        List<string> errors)
    {
        if (task.Limit is null)
        {
            errors.Add($"Limit for task {task.Id} is empty");
            return;
        }

        if (task.Limit.Priority.HasValue && !weights.ContainsKey(task.Limit.Priority.Value))
        {
            errors.Add($"Priority {task.Limit.Priority.Value} for task {task.Id} is invalid");
        }

        // Обязательные теги проверяются для всех листьев без исключения
        // (в отличие от ресурса ниже) - даже отключённая/завершённая задача
        // должна попадать в статистику, поэтому и её теги должны быть заполнены.
        foreach (var requiredKey in RequiredTaskTags.Keys)
        {
            if (task.Tags is null || !task.Tags.ContainsKey(requiredKey))
            {
                errors.Add($"Tag '{requiredKey}' is not set for task {task.Id}");
            }
        }

        // Отключённым/завершённым задачам Solver исполнителя не подбирает
        // (см. Solver.CreateTasksIntervals), так что для них это не ошибка.
        if (!task.CanSkipTask && !ResourceResolves(task.Limit, resources))
        {
            errors.Add($"Can't find resource for task {task.Id}");
        }

        foreach (var predecessorId in task.Limit.PredecessorIds)
        {
            if (!treeIndex.Contains(predecessorId))
            {
                errors.Add($"Task {task.Id} refers to unknown predecessor {predecessorId}");
            }
        }
    }

    private static bool ResourceResolves(TaskLimitDto limit, IReadOnlyCollection<ResourceDto> resources)
    {
        if (!string.IsNullOrWhiteSpace(limit.ResourceName))
        {
            return resources.Any(r => r.Name == limit.ResourceName);
        }

        return resources.Any(r => r.Role == limit.ResourceRole);
    }

    // Разворачивает PredecessorIds всех листьев в рёбра leaf -> leaf через
    // TaskTreeIndex (группа разворачивается в свои листья точно так же, как
    // при последующем построении модели) и ищет цикл обычным DFS с
    // трёхцветной покраской вершин.
    private static void ValidateNoCycles(TaskTreeIndex treeIndex, List<string> errors)
    {
        var visited = new HashSet<string>();

        foreach (var leafId in treeIndex.Leaves.Keys)
        {
            var path = new List<string>();
            if (HasCycle(leafId, treeIndex, visited, new HashSet<string>(), path))
            {
                errors.Add($"Circular dependency: {string.Join(" -> ", path)}");
                return;
            }
        }
    }

    private static bool HasCycle(
        string id,
        TaskTreeIndex treeIndex,
        HashSet<string> visited,
        HashSet<string> inProgress,
        List<string> path)
    {
        if (inProgress.Contains(id))
        {
            path.Add(id);
            return true;
        }

        if (!visited.Add(id))
        {
            return false;
        }

        inProgress.Add(id);
        path.Add(id);

        if (treeIndex.Leaves.TryGetValue(id, out var task) && task.Limit is not null)
        {
            foreach (var predecessorId in task.Limit.PredecessorIds)
            {
                if (!treeIndex.Contains(predecessorId))
                {
                    continue; // об этом уже сообщено отдельной ошибкой
                }

                foreach (var leafId in treeIndex.ExpandToLeafIds([predecessorId]))
                {
                    if (HasCycle(leafId, treeIndex, visited, inProgress, path))
                    {
                        return true;
                    }
                }
            }
        }

        inProgress.Remove(id);
        path.RemoveAt(path.Count - 1);

        return false;
    }
}
