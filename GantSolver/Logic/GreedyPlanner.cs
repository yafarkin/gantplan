using GantPlan.Dtos;

namespace GantPlan.Logic;

// Жадный план: задачи по одной, в порядке готовности (все
// предшественники уже расставлены), а среди готовых - по приоритету и
// порядку в дереве; каждая ставится тому из подходящих исполнителей, у
// кого она раньше закончится, в первое свободное окно. Соблюдает те же
// жёсткие ограничения, что и модель, кроме DueDate. Нужен Solver'у для
// двух вещей: его срок - основа горизонта, а сам план - подсказка CP-SAT,
// чтобы у того сразу было допустимое решение, а не поиск с нуля.
internal static class GreedyPlanner
{
    // null - если какую-то задачу не удалось поставить в MaxHorizon.
    public static Dictionary<string, (int Start, int End, string Resource)>? Build(PlanningContext context)
    {
        // Занятость исполнителя - как в Solver.CreateResourceRestrictions: до
        // AvailFrom и после AvailTo он недоступен.
        var busy = context.Project.Resources.ToDictionary(r => r.Name, r =>
        {
            var intervals = new List<(int Start, int End)>();
            if (r.AvailFrom is not null)
            {
                intervals.Add((int.MinValue, context.Offset(r.AvailFrom.Value)));
            }

            if (r.AvailTo is not null)
            {
                intervals.Add((context.Offset(r.AvailTo.Value) + 1, int.MaxValue));
            }

            return intervals;
        });

        var plan = new Dictionary<string, (int Start, int End, string Resource)>();
        var tasks = context.Alignment.FlattenTasksToSolve;

        // Уже начатые задачи стоят там, где стоят.
        foreach (var (taskKey, fixedTask) in context.FixedTasks)
        {
            if (context.CalcTaskEnd(fixedTask.Resource, fixedTask.Start, fixedTask.Duration) is not { } end)
            {
                return null;
            }

            plan[taskKey] = (fixedTask.Start, end, fixedTask.Resource.Name);
            busy[fixedTask.Resource.Name].Add((fixedTask.Start, end));
        }

        var treeOrder = tasks.Keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);

        while (plan.Count < tasks.Count)
        {
            var (taskKey, task) = tasks
                .Where(t => !plan.ContainsKey(t.Key) && t.Value.Limit!.PredecessorIds.All(plan.ContainsKey))
                .OrderByDescending(t => context.Weights[t.Value.Limit!.Priority ?? 0])
                .ThenBy(t => treeOrder[t.Key])
                .First();

            var earliest = Math.Max(0, context.CalcMinStart(task));
            foreach (var predecessorId in task.Limit!.PredecessorIds)
            {
                earliest = Math.Max(earliest, plan[predecessorId].End);
            }

            (int Start, int End, string Resource)? best = null;
            foreach (var resource in context.FindResourcesForTask(task))
            {
                var slot = FindFreeSlot(context, resource, busy[resource.Name],
                    Math.Max(earliest, context.HistoryEnd.GetValueOrDefault(resource.Name)),
                    context.CalcTaskLimitDuration(task, resource));
                if (slot is not null && (best is null || slot.Value.End < best.Value.End))
                {
                    best = (slot.Value.Start, slot.Value.End, resource.Name);
                }
            }

            if (best is null)
            {
                return null;
            }

            plan[taskKey] = best.Value;
            busy[best.Value.Resource].Add((best.Value.Start, best.Value.End));
        }

        return plan;
    }

    // Первое окно не раньше earliest: старт в рабочий день, конец - по
    // PlanningContext.CalcTaskEnd, без пересечения с занятостью исполнителя.
    private static (int Start, int End)? FindFreeSlot(
        PlanningContext context, ResourceDto resource, List<(int Start, int End)> busy, int earliest, int duration)
    {
        var calendar = context.Calendars[resource.Name];
        var start = earliest;
        while (start < PlanningContext.MaxHorizon)
        {
            if (!calendar.IsWorkingDay(start))
            {
                start++;
                continue;
            }

            if (context.CalcTaskEnd(resource, start, duration) is not { } end)
            {
                return null;
            }

            var s = start;
            var conflictEnd = busy.Where(b => b.Start < end && s < b.End).Select(b => (int?)b.End).Max();
            if (conflictEnd is null)
            {
                return (start, end);
            }

            if (conflictEnd.Value >= PlanningContext.MaxHorizon)
            {
                return null;
            }

            start = conflictEnd.Value;
        }

        return null;
    }
}
