using GantPlan.Dtos;
using Mapster;

namespace GantPlan.Logic;

// Делает solver-безопасные копии листовых задач. Дальше PredecessorResolver
// и Solver работают только с этими копиями - исходное дерево, которое
// передал вызывающий код, после этого места больше не меняется вплоть до
// Solver.PostFillAfterSolve (где в него осознанно дописываются Fact/Plan).
public static class LeafTaskCopier
{
    public static Dictionary<string, TaskDto> Copy(IReadOnlyDictionary<string, TaskDto> leaves)
    {
        var result = new Dictionary<string, TaskDto>();

        foreach (var (id, task) in leaves)
        {
            result.Add(id, task.Adapt<TaskDto>());
        }

        return result;
    }
}
