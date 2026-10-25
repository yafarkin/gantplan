using GantPlan.Dtos;

namespace GantPlan.Logic;

// План и факт. Fact.Records - журнал хода работ. Вручную вводится то, что
// известно: старт, остаток, пауза и - обязательно - закрытие (Completed).
// По FactDate солвер сам дописывает автоматический прогресс (IsAuto):
// старт по плану и остаток на дату среза. Перед каждым решением авто-записи
// удаляются и считаются заново - планирование опирается только на ручные.
// Закрытие солвер сам не ставит никогда: незакрытая задача не может
// закончиться раньше ближайшего рабочего дня после FactDate (см.
// PlanningContext.CalcTaskEnd) - если по плану она уже должна была быть
// сделана, она "ждёт подтверждения закрытия" и держит исполнителя и
// последователей.
internal sealed class FactProgress(PlanningContext context)
{
    // Авто-записи прошлого прогона (если план уже решали) - не факт, а
    // расчёт: убираем, чтобы планировать только по ручным записям.
    public static void RemoveAutoFacts(TaskDto? task)
    {
        if (task is null)
        {
            return;
        }

        if (task.Fact is not null)
        {
            task.Fact.Records.RemoveAll(r => r.IsAuto);
            if (task.Fact.Records.Count == 0)
            {
                task.Fact = null;
            }
        }

        foreach (var child in task.Children)
        {
            RemoveAutoFacts(child);
        }
    }

    // Граница истории исполнителя - день после его последнего подтверждённого
    // закрытия: раньше неё ему не ставится ничего неначатого. Закрытые задачи
    // в модели не участвуют (CanSkipTask), и без этой границы их время в
    // прошлом выглядело бы свободным - солвер ставил бы туда совсем другие
    // задачи. Совсем новую задачу от постановки "задним числом" позже
    // последнего закрытия защищает StartAfter.
    public void CalcHistoryEnd(List<string> warnings)
    {
        context.HistoryEnd.Clear();
        foreach (var task in context.Alignment.OriginalTasks.Values.Where(t => !t.HasChild))
        {
            var completed = task.Fact?.Records.LastOrDefault(r => r.Type == TaskFactRecordType.Completed);
            if (completed is null)
            {
                continue;
            }

            if (FindCompletedBy(task) is not { } resource)
            {
                warnings.Add($"Задача {task.Id} закрыта, но неизвестно, кем: граница истории исполнителя " +
                             "не учтена - укажите ResourceName в записи Completed");
                continue;
            }

            var end = context.Offset(completed.RecordedAt) + 1;
            context.HistoryEnd[resource.Name] = Math.Max(context.HistoryEnd.GetValueOrDefault(resource.Name), end);
        }
    }

    // Кто делал закрытую задачу: из записи о закрытии, иначе из последней
    // записи с исполнителем, иначе из лимитов или заданного руками Plan.
    // Если имени нигде нет, но в лимитах роль, и человек этой роли в
    // проекте один - значит, он. null - определить нельзя.
    private ResourceDto? FindCompletedBy(TaskDto task)
    {
        var name = task.Fact?.Records.LastOrDefault(r => r.Type == TaskFactRecordType.Completed)?.ResourceName
                   ?? task.Fact?.Records.LastOrDefault(r => r.ResourceName is not null)?.ResourceName
                   ?? task.Limit?.ResourceName
                   ?? task.Plan?.ResourceName;

        if (name is not null)
        {
            return context.Project.Resources.FirstOrDefault(r => r.Name == name);
        }

        var byRole = context.Project.Resources.Where(r => r.Role == task.Limit?.ResourceRole).ToList();
        return byRole.Count == 1 ? byRole[0] : null;
    }

    // Уже начатая задача: старт - дата последней ручной записи прогресса
    // (Started - работа в этот день только начата; прочие записи - день уже
    // отработан, остаток считается со следующего дня), сдвинутая на
    // ближайший рабочий день исполнителя; исполнитель - тот, кто её делал
    // (см. PlanningContext.FindResourcesForTask); остаток - из той же записи
    // или, если он не указан, из лимитов задачи.
    public void PrepareFixedTasks()
    {
        context.FixedTasks.Clear();
        foreach (var (taskKey, task) in context.Alignment.FlattenTasksToSolve)
        {
            if (task.Fact?.IsProgress != true)
            {
                continue;
            }

            var resource = context.FindResourcesForTask(task).Single();
            var lastProgressRec = FindLastProgressRecord(task.Fact);

            var start = context.Offset(lastProgressRec.RecordedAt);
            if (lastProgressRec.Type != TaskFactRecordType.Started)
            {
                start++;
            }

            start = (context.Calendars[resource.Name].CalcEndOffset(start, 1)
                     ?? throw new Exception($"Task {taskKey} is in progress, but its resource has no working days ahead")) - 1;

            var duration = lastProgressRec.Duration ?? context.CalcTaskLimitDuration(task, resource);
            context.FixedTasks[taskKey] = new FixedTask(start, duration, resource);
        }
    }

    // Автоматический прогресс на дату среза (записи с IsAuto, см.
    // TaskFactRecordDto.IsAuto): если по плану задача к FactDate уже начата,
    // а вручную её старт не отмечен - Started на плановую дату старта; если
    // FactDate попадает внутрь задачи - InProgress на FactDate с остатком
    // рабочих дней после неё. Completed не пишется никогда - закрытие
    // подтверждается только вручную.
    public void AddAutoFacts(TaskDto task, int start, int end, int duration, ResourceDto resource, bool isStarted)
    {
        if (context.FactOffset is not { } factOffset || start > factOffset)
        {
            return;
        }

        task.Fact ??= new TaskFactDto();

        if (!isStarted)
        {
            task.Fact.Records.Add(new TaskFactRecordDto
            {
                RecordedAt = context.ToDate(start),
                Type = TaskFactRecordType.Started,
                ResourceName = resource.Name,
                Duration = duration,
                IsAuto = true
            });
        }

        // Последний день задачи по построению (CalcTaskEnd) всегда позже
        // FactDate - незакрытая задача на дату среза всегда ещё в работе.
        var lastProgressRec = FindLastProgressRecord(task.Fact);
        if (lastProgressRec.RecordedAt < context.Project.FactDate)
        {
            var remaining = context.Calendars[resource.Name]
                .CalcWorkingDaysCount(context.ToDate(factOffset + 1), context.ToDate(end - 1));

            task.Fact.Records.Add(new TaskFactRecordDto
            {
                RecordedAt = context.Project.FactDate!.Value,
                Type = TaskFactRecordType.InProgress,
                ResourceName = resource.Name,
                Duration = remaining,
                IsAuto = true
            });
        }
    }

    // Закрытые задачи в модели не участвуют. Если даты им не заданы руками,
    // восстанавливаем их для таймлайна: конец - дата закрытия, начало - дата
    // ручного Started или, если её нет, отсчёт оценки назад по календарю
    // исполнителя. Иначе после подтверждения закрытия задача просто
    // пропадала бы с диаграммы.
    public void FillFinishedTasksPlan()
    {
        foreach (var task in context.Alignment.OriginalTasks.Values.Where(t => !t.HasChild))
        {
            var completed = task.Fact?.Records.LastOrDefault(r => r.Type == TaskFactRecordType.Completed);
            if (completed is null || task.Plan is { PlannedStart: not null, PlannedFinish: not null })
            {
                continue;
            }

            var resource = FindCompletedBy(task);
            var finish = completed.RecordedAt;
            var start = task.Fact!.StartDate
                        ?? (resource is null || task.Limit is null
                            ? finish
                            : context.ToDate(context.Calendars[resource.Name].CalcStartOffset(
                                context.Offset(finish), context.CalcTaskLimitDuration(task, resource))));

            task.Plan = new TaskPlanDto
            {
                ResourceName = task.Plan?.ResourceName ?? resource?.Name,
                PlannedStart = start,
                PlannedFinish = finish
            };
        }
    }

    // Последняя запись прогресса (Started/InProgress/CorrectedDuration),
    // предпочтительно с известным остатком Duration. Если ни у одной записи
    // остатка нет (например, Started без Duration) - просто последняя
    // запись прогресса, а остаток тогда берётся из лимитов задачи.
    // Вызывается только для задач, у которых есть хотя бы одна запись
    // Started/InProgress.
    private static TaskFactRecordDto FindLastProgressRecord(TaskFactDto fact)
    {
        static bool IsProgressRecord(TaskFactRecordDto x) =>
            x.Type is TaskFactRecordType.Started or TaskFactRecordType.InProgress
                or TaskFactRecordType.CorrectedDuration;

        return fact.Records.LastOrDefault(x => IsProgressRecord(x) && x.Duration is not null)
               ?? fact.Records.Last(IsProgressRecord);
    }
}
