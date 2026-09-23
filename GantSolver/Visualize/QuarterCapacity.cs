namespace GantPlan.Visualize;

internal readonly record struct QuarterCapacity(int Quota, int Capacity, int Idle, int Overload)
{
    public static QuarterCapacity Calculate(int available, int business, int nonBusiness,
        double unplannedWorkReserveShare)
    {
        var quota = (int)Math.Round(available * unplannedWorkReserveShare, MidpointRounding.AwayFromZero);
        var capacity = available - quota;
        var free = capacity - business - nonBusiness;
        return new QuarterCapacity(quota, capacity, Math.Max(0, free), Math.Max(0, -free));
    }

    public static void ValidateReserveShare(double unplannedWorkReserveShare)
    {
        if (double.IsNaN(unplannedWorkReserveShare)
            || unplannedWorkReserveShare < 0 || unplannedWorkReserveShare > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(unplannedWorkReserveShare),
                "Доля резерва на внеплановые работы должна быть от 0 до 1.");
        }
    }
}
