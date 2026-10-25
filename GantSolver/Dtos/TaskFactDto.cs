namespace GantPlan.Dtos;

public sealed record TaskFactDto
{
    public List<TaskFactRecordDto> Records { get; init; } = new();

    public DateOnly? StartDate => Records.FirstOrDefault(x => x.Type == TaskFactRecordType.Started)?.RecordedAt;

    public DateOnly? FinishDate => Records.LastOrDefault(x => x.Type is TaskFactRecordType.Completed or TaskFactRecordType.Canceled)?.RecordedAt;

    public bool IsFinished => FinishDate.HasValue;

    public bool IsProgress => !IsFinished && Records.Any(x => x.Type is TaskFactRecordType.InProgress or TaskFactRecordType.Started);

    // На паузе - если последняя запись о ходе работы - это Paused. Работа
    // возобновляется следующей записью Started/InProgress/CorrectedDuration
    // (раньше хватало любой записи Paused в истории - и задача оставалась на
    // паузе навсегда).
    public bool IsPaused => Records.LastOrDefault(x => x.Type is TaskFactRecordType.Paused
        or TaskFactRecordType.Started or TaskFactRecordType.InProgress
        or TaskFactRecordType.CorrectedDuration)?.Type == TaskFactRecordType.Paused;
}
