namespace GantPlan.Dtos.Enums;

public enum TShirtType
{
    None = 0,
    
    /// <summary>
    /// 1 day
    /// </summary>
    XS,
    
    /// <summary>
    /// 2...3 days
    /// </summary>
    S,
    
    /// <summary>
    /// 4...7 days
    /// </summary>
    M,
    
    /// <summary>
    /// 8...15 days
    /// </summary>
    L,
    
    /// <summary>
    /// 15+ days
    /// </summary>
    XL
}

public static class TShirtTypeExtensions
{
    public static int ToDays(this TShirtType tShirtType) => tShirtType switch
    {
        TShirtType.XS => 1,
        TShirtType.S => 3,
        TShirtType.M => 7,
        TShirtType.L => 15,
        TShirtType.XL => 25,
        _ => throw new ArgumentOutOfRangeException(nameof(tShirtType), tShirtType, null)
    };
}