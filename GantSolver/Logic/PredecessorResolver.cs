using GantPlan.Dtos;

namespace GantPlan.Logic;

// Разворачивает PredecessorIds листовых копий: id группы задач превращается
// в id всех её листьев (см. TaskTreeIndex.ExpandToLeafIds), а предшественники,
// которые не будут решаться (Disabled/Paused/Finished), выбрасываются - на
// них и так не будет переменных start/end в модели солвера.
public static class PredecessorResolver
{
    public static void Resolve(TaskTreeIndex treeIndex, IReadOnlyDictionary<string, TaskDto> leafTasksCopy)
    {
        foreach (var task in leafTasksCopy.Values)
        {
            var expandedIds = treeIndex.ExpandToLeafIds(task.Limit!.PredecessorIds);

            var resolvedIds = new List<string>();
            foreach (var leafId in expandedIds)
            {
                if (!leafTasksCopy[leafId].CanSkipTask)
                {
                    resolvedIds.Add(leafId);
                }
            }

            task.Limit.PredecessorIds = resolvedIds.ToArray();
        }
    }
}
