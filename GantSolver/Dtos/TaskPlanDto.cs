namespace GantPlan.Dtos;

public sealed record TaskPlanDto
{
    public string ResourceName { get; set; } = null!;
    public DateOnly? PlannedStart { get; set; }
    public DateOnly? PlannedFinish { get; set; }
}