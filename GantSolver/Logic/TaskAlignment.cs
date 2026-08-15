using GantPlan.Dtos;

namespace GantPlan.Logic;

// Разворачивает дерево задач проекта в плоские списки, которые нужны
// Solver'у. Сам класс - только оркестрация; вся логика разнесена по
// отдельным шагам с одной ответственностью на каждый:
//   TaskTreeIndex           - структура дерева (id листьев/групп), без копий;
//   TaskAttributeInheritance- наследование Tags/Priority/Disabled вниз по дереву;
//   ProjectValidator        - бизнес-правила, от которых зависит Solver
//                             (собирает все проблемы разом, а не падает на первой);
//   LeafTaskCopier          - solver-безопасные копии листьев;
//   PredecessorResolver     - разворот зависимостей от группы задач в
//                             зависимости от всех её листьев.
public sealed class TaskAlignment
{
    public readonly Dictionary<string, TaskDto> OriginalTasks = new();
    public readonly Dictionary<string, TaskDto> FlattenTasksCopy = new();
    public readonly Dictionary<string, TaskDto> FlattenTasksToSolve = new();

    public void Alignment(ProjectDto project, Dictionary<int, long> weights)
    {
        OriginalTasks.Clear();
        FlattenTasksCopy.Clear();
        FlattenTasksToSolve.Clear();

        GuardProjectPreconditions(project, weights);

        var treeIndex = TaskTreeIndex.Build(project.RootTask);

        TaskAttributeInheritance.Apply(project.RootTask);

        var errors = ProjectValidator.Validate(treeIndex, weights, project.Resources);
        if (errors.Count > 0)
        {
            throw new Exception(string.Join("\n", errors));
        }

        foreach (var (id, task) in treeIndex.AllTasks)
        {
            OriginalTasks.Add(id, task);
        }

        foreach (var (id, task) in LeafTaskCopier.Copy(treeIndex.Leaves))
        {
            FlattenTasksCopy.Add(id, task);
        }

        PredecessorResolver.Resolve(treeIndex, FlattenTasksCopy);

        foreach (var (id, task) in FlattenTasksCopy)
        {
            if (!task.CanSkipTask)
            {
                FlattenTasksToSolve.Add(id, task);
            }
        }
    }

    // Проверки, без которых дальше нет смысла даже строить индекс дерева -
    // намеренно fail-fast (первая же проблема останавливает всё), в отличие
    // от ProjectValidator, который собирает бизнес-ошибки списком.
    private static void GuardProjectPreconditions(ProjectDto project, Dictionary<int, long> weights)
    {
        if (project.RootTask is null)
        {
            throw new Exception("Root task cannot be null");
        }

        if (project.FactDate is not null && project.ProjectStart > project.FactDate)
        {
            throw new Exception("Fact date should be great or equal that project date");
        }

        // Solver всегда считает objective как start * weights[Priority ?? 0]
        // (см. Solver.CreateIntervalsAndPriorityObjectives) - без дефолтного
        // веса на приоритет 0 это упадёт в середине решения, а не сразу.
        if (!weights.ContainsKey(0))
        {
            throw new Exception("Weights must contain a default entry for priority 0");
        }
    }
}
