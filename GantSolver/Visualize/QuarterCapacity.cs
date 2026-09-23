namespace GantPlan.Visualize;

internal readonly record struct QuarterCapacity(int Quota, int Capacity, int Idle, int Overload)
{
    public static QuarterCapacity Calculate(int available, int business, int nonBusiness, double vletPercent)
    {
        var quota = (int)Math.Round(available * vletPercent, MidpointRounding.AwayFromZero);
        var capacity = available - quota;
        var free = capacity - business - nonBusiness;
        return new QuarterCapacity(quota, capacity, Math.Max(0, free), Math.Max(0, -free));
    }

    public static void ValidatePercent(double vletPercent)
    {
        if (double.IsNaN(vletPercent) || vletPercent < 0 || vletPercent > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(vletPercent), "Квота «влёт» должна быть от 0 до 1.");
        }
    }
}
