using Scada.Data.Models;

namespace Scada.Web.Plugins.PlgMimBasicComp.Code;

public sealed record EnergyIntegral(double? Kwh, double CoveredMinutes, int SkippedIntervals, int Samples);
public static class ArchiveEnergy
{
    public static EnergyIntegral Calculate(TrendBundle trend, double wattsPerUnit)
    {
        if (!(wattsPerUnit > 0) || !double.IsFinite(wattsPerUnit)) throw new ArgumentException("Invalid power scale");
        double kwh = 0, covered = 0; int skipped = 0;
        for (int i = 1; i < trend.Timestamps.Count; i++)
        {
            var a = trend.Trends[0][i - 1]; var b = trend.Trends[0][i];
            double seconds = (trend.Timestamps[i] - trend.Timestamps[i - 1]).TotalSeconds;
            if (!OperationsEngine.Good(a.Stat) || !OperationsEngine.Good(b.Stat) || !double.IsFinite(a.Val) || !double.IsFinite(b.Val) || a.Val < 0 || b.Val < 0 || seconds <= 0 || seconds > 180)
            { skipped++; continue; }
            kwh += (a.Val + b.Val) / 2 * seconds / 3600 * wattsPerUnit / 1000;
            covered += seconds / 60;
        }
        return new(covered > 0 ? kwh : null, covered, skipped, trend.Timestamps.Count);
    }
}
