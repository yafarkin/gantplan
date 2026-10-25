using GantPlan.Dtos;
using GantPlan.Dtos.Enums;

namespace GantPlan.Logic;

// Общее для всех частей планирования (GreedyPlanner, FactProgress, Solver)
// состояние одного решения: проект, календари ресурсов, дата среза,
// уже начатые задачи, граница истории исполнителей - и расчёты поверх него,
// которые должны быть одинаковыми везде (конец задачи, длительность,
// кандидаты-исполнители).
internal sealed class PlanningContext
{
    // Потолок горизонта планирования (~10 лет) - защита от вырожденных
    // входных данных, а не реальное ограничение.
    public const int MaxHorizon = 3660;

    public PlanningContext(ProjectDto project, TaskAlignment alignment, IReadOnlyDictionary<int, long> weights)
    {
        Project = project;
        Alignment = alignment;
        Weights = weights;
        FactOffset = project.FactDate is { } factDate ? Offset(factDate) : null;

        foreach (var resource in project.Resources)
        {
            Calendars[resource.Name] =
                new CalendarLogic(MaxHorizon, project.ProjectStart, project.GlobalCalendar, resource.Calendar);
        }
    }

    public ProjectDto Project { get; }

    public TaskAlignment Alignment { get; }

    // Веса приоритетов (см. Solver._weights).
    public IReadOnlyDictionary<int, long> Weights { get; }

    // Календари ресурсов на весь MaxHorizon.
    public Dictionary<string, CalendarLogic> Calendars { get; } = new();

    // FactDate в днях от ProjectStart; null - даты среза нет.
    public int? FactOffset { get; }

    // Уже начатые (по ручным записям факта) задачи: старт, исполнитель и
    // остаток известны, в модели это константы (см.
    // FactProgress.PrepareFixedTasks).
    public Dictionary<string, FixedTask> FixedTasks { get; } = new();

    // Граница истории исполнителя: день после его последнего подтверждённого
    // закрытия (см. FactProgress.CalcHistoryEnd).
    public Dictionary<string, int> HistoryEnd { get; } = new();

    public int Offset(DateOnly date) => date.DayNumber - Project.ProjectStart.DayNumber;

    public DateOnly ToDate(int offset) => Project.ProjectStart.AddDays(offset);

    // Конец задачи (полуинтервал [start, end)) у исполнителя при старте в
    // start: duration рабочих дней по его календарю. Плюс правило закрытия:
    // незакрытая задача, начатая не позже ближайшего рабочего дня после
    // FactDate, не может закончиться раньше этого дня - если по плану она
    // уже должна была быть сделана, она тянется до него (ждёт
    // подтверждения закрытия). null - не влезает в MaxHorizon.
    public int? CalcTaskEnd(ResourceDto resource, int start, int duration)
    {
        var calendar = Calendars[resource.Name];
        if (calendar.CalcEndOffset(start, duration) is not { } end)
        {
            return null;
        }

        if (FactOffset is { } factOffset)
        {
            if (calendar.CalcEndOffset(factOffset + 1, 1) is not { } closureEnd)
            {
                return null;
            }

            if (start < closureEnd)
            {
                end = Math.Max(end, closureEnd);
            }
        }

        return end;
    }

    // Самый ранний допустимый старт ещё не начатой задачи без учёта
    // предшественников и занятости исполнителя: StartAfter.
    public int CalcMinStart(TaskDto task)
    {
        return task.Limit!.StartAfter is { } startAfter ? Offset(startAfter) : 0;
    }

    // Рабочая (бизнес-дневная) длительность задачи для конкретного ресурса:
    // явно заданная Duration или оценка по TShirt-размеру с поправкой на
    // уверенность оценки ресурса (Confidence: 100% - нижняя граница, 0% -
    // верхняя), затем прибавляется буфер, затем длительность увеличивается
    // пропорционально неполной загрузке ресурса на задаче (Percent) с
    // округлением вверх: неполный день - всё равно занятый день (5 дней при
    // 80% - это 6.25, то есть 7, а не 6).
    public int CalcTaskLimitDuration(TaskDto task, ResourceDto resource)
    {
        var duration = task.Limit!.Duration ?? task.Limit.TShirt!.Value.ToDays(resource.Confidence);

        if (task.Limit.Buffer is not null)
        {
            duration += task.Limit.Buffer.Value;
        }

        if (resource.Percent != 100)
        {
            duration = Math.Max(1, Convert.ToInt32(Math.Ceiling(duration * 100.0 / resource.Percent)));
        }

        return duration;
    }

    // Определяет список кандидатов-ресурсов, среди которых солвер будет
    // выбирать исполнителя задачи. Приоритет:
    // 1) если задача уже в работе - только тот, кто её реально делал
    //    последний раз (менять исполнителя нельзя; сменить можно записью
    //    факта с другим ResourceName, например ChangedAssignee);
    // 2) иначе, если в лимитах явно указан ResourceName - только он;
    // 3) иначе - все ресурсы с подходящей ResourceRole (солвер сам выберет
    //    из них наиболее подходящего по календарю/загрузке).
    public List<ResourceDto> FindResourcesForTask(TaskDto task)
    {
        List<ResourceDto> resources;
        var resourceName = task.Limit!.ResourceName;

        if (task.Fact is not null && task.Fact.IsProgress)
        {
            // trying to find, who actually do this task last time
            var lastAssigneeRec = task.Fact.Records.LastOrDefault(x => x.ResourceName is not null);
            if (lastAssigneeRec is not null)
            {
                resourceName = lastAssigneeRec.ResourceName;
            }
        }

        if (!string.IsNullOrWhiteSpace(resourceName))
        {
            var resource = Project.Resources.SingleOrDefault(r => r.Name == resourceName);
            if (resource is null)
            {
                throw new Exception($"Can't find resource {resourceName} for task {task.Id}");
            }

            resources = [resource];
        }
        else
        {
            // all resources for this role
            resources = Project.Resources
                .Where(r => r.Role == task.Limit!.ResourceRole)
                .ToList();
        }

        return resources;
    }
}

internal sealed record FixedTask(int Start, int Duration, ResourceDto Resource);
