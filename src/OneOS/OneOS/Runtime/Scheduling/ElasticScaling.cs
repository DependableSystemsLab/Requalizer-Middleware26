using System;

namespace OneOS.Runtime.Scheduling;

// The scaling rule for elastic groups (S§10.5; the policy itself is implementation-defined):
// provision enough instances for the larger of the measured input rate and the group's min_rate
// goal at 80% of the profiled per-instance throughput, within the group's bounds. Without a
// throughput estimate the group keeps its size.
public static class ElasticScaling
{
    public const double TargetUtilization = 0.8;

    public static int Desired(int current, int min, int max, double measuredRate, double? throughputPerInstance, double? minRateGoal)
    {
        if (throughputPerInstance is not double thr || thr <= 0) return Math.Clamp(current, min, max);
        double demand = Math.Max(measuredRate, minRateGoal ?? 0);
        int want = (int)Math.Ceiling(demand / (thr * TargetUtilization));
        return Math.Clamp(Math.Max(want, 1), min, max);
    }
}
