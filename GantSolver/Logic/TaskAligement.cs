using GantPlan.Dtos;
using Mapster;

namespace GantPlan.Logic;

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
        
        ValidateAndFill(project, weights);
    }
    
    private void ValidateAndFill(ProjectDto project, Dictionary<int, long> weights)
    {
        if (project.FactDate is not null && project.ProjectStart > project.FactDate)
        {
            throw new Exception("Fact date should be great or equal that project date");
        }

        if (project.RootTask is null)
        {
            throw new Exception("Root task cannot be null");
        }

        ValidateAndFill(null, project.RootTask, weights);
        
        if (FlattenTasksToSolve.Count == 0)
        {
            throw new Exception("No tasks found");
        }

        PostFill(project.RootTask);
    }

    private void ValidateAndFill(TaskDto? parentTask, TaskDto task, Dictionary<int, long> weights)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
        {
            throw new Exception("Task must have an id");
        }
        
        if (FlattenTasksCopy.ContainsKey(task.Id) || !OriginalTasks.TryAdd(task.Id, task))
        {
            throw new Exception($"Task with id {task.Id} already exists");
        }
        
        task.WorkType ??= parentTask?.WorkType;
        task.IsOkr ??= parentTask?.IsOkr;

        if (task.Limit is not null)
        {
            task.Limit.Priority ??= parentTask?.Limit?.Priority;
        }
        
        if (parentTask is not null && parentTask.Disabled)
        {
            task.Disabled = parentTask.Disabled;
        }

        if (task.HasChild)
        {
            foreach (var child in task.Children)
            {
                ValidateAndFill(task, child, weights);
            }

            return;
        }

        if (task.Limit is null)
        {
            throw new Exception($"Limit for task {task.Id} is empty");
        }
        
        if (task.Limit.Priority.HasValue && !weights.ContainsKey(task.Limit.Priority.Value))
        {
            throw new Exception($"Priority {task.Limit.Priority.Value} for task {task.Id} is invalid");
        }

        var taskCopy = task.Adapt<TaskDto>();
        
        FlattenTasksCopy.Add(task.Id, taskCopy);

        if (!taskCopy.CanSkipTask)
        {
            FlattenTasksToSolve.Add(task.Id, taskCopy);
        }
    }
    
    private void PostFill(TaskDto task)
    {
        if (task.HasChild)
        {
            foreach (var child in task.Children)
            {
                PostFill(child);
            }

            return;
        }

        task = FlattenTasksCopy[task.Id];
        
        var predecessorIds = new List<string>();
        foreach (var predecessorId in task.Limit!.PredecessorIds)
        {
            var preIds = new List<string>();

            if (FlattenTasksCopy.ContainsKey(predecessorId))
            {
                preIds.Add(predecessorId);
            }
            else
            {
                var groupTask = OriginalTasks[predecessorId];
                preIds = GetChildrenPredecessors(groupTask);
            }

            foreach (var preId in preIds)
            {
                var taskCopy = FlattenTasksCopy[preId];
                if (!taskCopy.CanSkipTask)
                {
                    predecessorIds.Add(preId);
                }
            }
        }

        task.Limit.PredecessorIds = predecessorIds.ToArray();
    }
    
    private List<string> GetChildrenPredecessors(TaskDto task)
    {
        var result = new List<string>();

        if (task.HasChild)
        {
            foreach (var child in task.Children)
            {
                result.AddRange(GetChildrenPredecessors(child));
            }
        }
        else
        {
            result.AddRange(task.Limit!.PredecessorIds);
        }

        return result;
    }
}