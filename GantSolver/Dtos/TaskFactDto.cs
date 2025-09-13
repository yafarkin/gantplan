namespace GantPlan.Dtos;

public sealed record TaskFactDto
{
    public List<TaskFactRecordDto> Records { get; init; } = new();

    public DateOnly? StartDate => Records.SingleOrDefault(x => x.Type == TaskFactRecordType.Started)?.RecordedAt;
    
    public DateOnly? FinishDate => Records.SingleOrDefault(x => x.Type is TaskFactRecordType.Completed or TaskFactRecordType.Canceled)?.RecordedAt;
    
    public bool IsFinished => FinishDate.HasValue;
    
    public bool IsProgress => !IsFinished && Records.Any(x => x.Type is TaskFactRecordType.InProgress or TaskFactRecordType.Started);
    
    public bool IsPaused => Records.LastOrDefault(x => x.Type == TaskFactRecordType.Paused) is not null;
}