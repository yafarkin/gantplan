using GantPlan.Dtos;

namespace GantPlan.Logic;

// Протаскивает вниз по дереву значения, которые не заданы явно у ребёнка:
// произвольные теги (Tags), приоритет из Limit.Priority и признак Disabled
// (once disabled, все потомки тоже disabled). Работает прямо на исходном
// дереве (project.RootTask) - это осознанная мутация входных данных, а не
// побочный эффект валидации, как было раньше.
public static class TaskAttributeInheritance
{
    public static void Apply(TaskDto root) => Apply(null, root);

    private static void Apply(TaskDto? parent, TaskDto task)
    {
        if (parent?.Tags is { Count: > 0 })
        {
            task.Tags ??= new Dictionary<string, string>();
            foreach (var (key, value) in parent.Tags)
            {
                task.Tags.TryAdd(key, value);
            }
        }

        if (task.Limit is not null)
        {
            task.Limit.Priority ??= parent?.Limit?.Priority;
        }

        if (parent is { Disabled: true })
        {
            task.Disabled = true;
        }

        foreach (var child in task.Children)
        {
            Apply(task, child);
        }
    }
}
