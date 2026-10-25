using GantPlan.Dtos;
using GantPlan.Dtos.Enums;

namespace Tests;

// Общие заготовки для TaskAlignmentTests и ProjectValidatorTests.
internal static class TaskTestData
{
    public static readonly Dictionary<int, long> DefaultWeights = new() { { 0, 1 } };

    public static ResourceDto DevResource(string name = "john") => new()
    {
        Role = "dev",
        Name = name
    };

    public static TaskLimitDto SimpleLimit(params string[] predecessorIds) => new()
    {
        TShirt = TShirtType.S,
        ResourceRole = "dev",
        PredecessorIds = predecessorIds
    };

    // Обязательный тег (RequiredTaskTags), выставленный на корне - дальше
    // наследуется всем листьям, так что отдельные задачи в тестах его не
    // задают.
    public static Dictionary<string, string> RequiredTags() => new()
    {
        [TaskTagKeys.IsOkr] = "false"
    };
}
