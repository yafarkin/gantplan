namespace GantPlan.Dtos;

public enum TaskFactRecordType
{
    None = 0,
    
    Started = 1,
    
    InProgress = 2,
    
    Completed = 3,
    
    Canceled = 4,
    
    ChangedAssignee = 5,
    
    CorrectedDuration = 6,
    
    Other = 7,
    
    Paused = 8,
}