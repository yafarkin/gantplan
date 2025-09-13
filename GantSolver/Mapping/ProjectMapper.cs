using GantPlan.Dtos;
using GantPlan.Dtos.OldVisualize;

namespace GantPlan.Mapping;

public static class ProjectDtoMapper
{
    public static RootDto MapToRootDto(ProjectDto projectDto)
    {
        var resources = MapResources(projectDto);
        var globalNonWorking = ConvertPeriodsToDate(projectDto.GlobalCalendar?.NonWorkingDays);
        var resourceNonWorking = projectDto.Resources
            .Where(r => r.Calendar != null)
            .ToDictionary(
                r => $"{r.Name}-{r.Role}",
                r => ConvertPeriodsToDate(r.Calendar?.NonWorkingDays)
            );

        var rootTask = MapRootTask(projectDto);

        var result = new RootDto
        {
            Tasks = [rootTask],
            Resources = resources,
            GlobalNonWorking = globalNonWorking,
            ResourceNonWorking = resourceNonWorking
        };

        return result;
    }

    private static OldTaskDto MapRootTask(ProjectDto project)
    {
        var tasks = MapChildrenTask(project, project.RootTask);

        var id = project.RootTask.Id;
        var name = project.RootTask.Name;
        var start = tasks.Select(x => x.Start).Min();
        var end = tasks.Select(x => x.End).Max();

        var result = new OldTaskDto
        {
            Id = id,
            Name = name,
            Start = start,
            End = end,
            Duration = null,
            Buffer = null,
            Employee = null,
            Tasks = tasks,
            Predecessors = null
        };
        return result;
    }

    private static OldResourceDto[] MapResources(ProjectDto project)
    {
        var result = new List<OldResourceDto>();
        foreach (var resource in project.Resources)
        {
            var oldResourceDto = MapResource(project, resource);
            result.Add(oldResourceDto);
        }

        return result.ToArray();
    }

    private static OldTaskDto[] MapChildrenTask(ProjectDto project, TaskDto taskWithChildren)
    {
        var result = new List<OldTaskDto>();
        foreach (var task in taskWithChildren.Children)
        {
            if (task.HasChild)
            {
                result.AddRange(MapChildrenTask(project, task));
            }
            else
            {
                var oldTaskDto = MapTask(project, task);
                if (oldTaskDto is not null)
                {
                    result.Add(oldTaskDto);
                }
            }
        }

        return result.ToArray();
    }

    private static OldTaskDto? MapTask(ProjectDto project, TaskDto taskDto)
    {
        if (taskDto.Plan is null)
        {
            return null;
        }

        var id = taskDto.Id;
        var name = $"{taskDto.JiraKey}: {taskDto.Name}";
        var start = taskDto.Plan?.PlannedStart ??
                    throw new InvalidOperationException("PlannedStart is required");
        var end = taskDto.Plan?.PlannedFinish ??
                  throw new InvalidOperationException("PlannedFinish is required");
        
        var duration = taskDto.Plan.PlannedFinish.Value.DayNumber - taskDto.Plan.PlannedStart.Value.DayNumber; 
        
        var buffer = taskDto.Limit?.Buffer;

        var resource = project.Resources.Single(x => x.Name == taskDto.Plan.ResourceName);

        var employee = $"{resource.Name}-{resource.Role}";

        var predecessors = taskDto.Limit!.PredecessorIds.Any() ? taskDto.Limit.PredecessorIds.ToArray() : null;

        var result = new OldTaskDto
        {
            Id = id,
            Name = name,
            Start = start,
            End = end,
            Duration = duration,
            Buffer = buffer,
            Employee = employee,
            Tasks = null,
            Predecessors = predecessors
        };

        return result;
    }

    private static OldResourceDto MapResource(ProjectDto project, ResourceDto resourceDto)
    {
        var name = $"{resourceDto.Name}-{resourceDto.Role}";

        var allTasks = project.RootTask.Children
            .SelectMany(x => x.Children)
            .Where(x => x.Plan?.ResourceName == resourceDto.Name)
            .ToList();

        var start = allTasks.Min(x => x.Plan!.PlannedStart).GetValueOrDefault();
        var end = allTasks.Max(x => x.Plan!.PlannedFinish).GetValueOrDefault();

        var totalDays = end.DayNumber - start.DayNumber;

        var workDays = CalcWorkingDays(resourceDto, project, start, end);

        var result = new OldResourceDto
        {
            Name = name,
            Start = start,
            End = end,
            TotalDays = totalDays,
            WorkDays = workDays
        };

        return result;
    }

    private static int CalcWorkingDays(ResourceDto resourceDto, ProjectDto project, DateOnly from, DateOnly to)
    {
        var result = 0;

        var d = from;
        while (d <= to)
        {
            var workDay = d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

            if (project.GlobalCalendar is not null)
            {
                if (workDay && project.GlobalCalendar.NonWorkingDays is not null)
                {
                    var p = project.GlobalCalendar.NonWorkingDays.SingleOrDefault(x => d >= x.From && d <= x.To);
                    if (p is not null)
                    {
                        workDay = false;
                    }
                }

                if (!workDay && project.GlobalCalendar.WorkingDays is not null)
                {
                    var p = project.GlobalCalendar.WorkingDays.SingleOrDefault(x => d >= x.From && d <= x.To);
                    if (p is not null)
                    {
                        workDay = true;
                    }
                }
            }

            d = d.AddDays(1);
            if (workDay)
            {
                result++;
            }
        }
        
        // TODO: Add a per‑day calculation for the resource

        return result;
    }

    private static DateOnly[] ConvertPeriodsToDate(ICollection<CalendarPeriod>? periods)
    {
        var result = new List<DateOnly>();
        if (periods is not null)
        {
            foreach (var period in periods)
            {
                var day = period.From;
                var to = period.To;

                while (day <= to)
                {
                    result.Add(day);
                    day = day.AddDays(1);
                }
            }
        }

        return result.ToArray();
    }
}