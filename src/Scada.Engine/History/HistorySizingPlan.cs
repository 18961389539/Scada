using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Options;
using Scada.Engine.Maintenance;

namespace Scada.Engine.History;

// Planning scenario, NOT a prediction: assume a changed value on every configured scan.
// Do not discount deadbands: their actual effect needs measured plant data.
public sealed record HistorySizingPlan(int ArchivedTags, int UnmodelledDerivedTags, double SamplesPerSecond, double RawScenarioBytes)
{
    public static HistorySizingPlan Estimate(IReadOnlyList<Device> devices, IReadOnlyList<Tag> tags, ScadaSettings settings)
    {
        var active = devices.Where(x => x.Enabled).ToDictionary(x => x.Id);
        int count = 0, derived = 0;
        double rate = 0, bytesPerSecond = 0;
        foreach (var tag in tags.Where(x => x.ArchiveMode != ArchiveMode.None))
        {
            double intervalMs;
            if (tag.Kind == TagKind.Physical)
            {
                if (tag.DeviceId is not int id || !active.TryGetValue(id, out var device)) continue;
                intervalMs = ScanCadence.Effective(device, tag);
            }
            else if (tag.Kind == TagKind.Calculated)
            {
                if (!tag.CalculationEnabled) continue;
                intervalMs = Math.Max(10, tag.CalculationIntervalMs);
            }
            else { derived++; continue; }
            var perSecond = 1000d / intervalMs;
            // Periodic is "change OR timer", not a cap on archive frequency.
            if (tag.ArchiveMode == ArchiveMode.Periodic && tag.DataType != TagDataType.Bool)
                perSecond += 1d / Math.Max(1, tag.ArchivePeriodSec);
            count++;
            rate += perSecond;
            var assumedBytes = 100d + (tag.DataType == TagDataType.String ? Math.Max(tag.TextLength, 256) * 4d : 0);
            bytesPerSecond += perSecond * assumedBytes;
        }
        // Month-file retention keeps the entire cutoff month: include one additional month.
        return new(count, derived, rate, bytesPerSecond * 86400d * 30.44d * (Math.Max(settings.HistoryRetentionMonths, 1) + 1));
    }

    public IEnumerable<SystemWarning> Warnings(ScadaSettings settings, HistoryCapacity history)
    {
        if (ArchivedTags == 0 && UnmodelledDerivedTags == 0) yield break;
        if (ArchivedTags + UnmodelledDerivedTags >= 1000 && settings.HistorySizeWarnGb <= 0)
            yield return new("history_size_guard_disabled", "At least 1000 tags have archiving enabled, but HistorySizeWarnGb is disabled. Set a site-specific size warning; review docs/history-capacity.md and the Medium example before commissioning.");
        var journalAllowance = RawScenarioBytes * 1.3;
        var threshold = settings.HistorySizeWarnGb > 0 ? settings.HistorySizeWarnGb * 1024d * 1024 * 1024 : double.PositiveInfinity;
        var diskBudget = history.Disk.FreeBytes is long free ? Math.Max(0d, free + (double)history.RawBytes - settings.DiskFreeWarnGb * 1024d * 1024 * 1024) : double.PositiveInfinity;
        if (journalAllowance > Math.Min(threshold, diskBudget))
            yield return new("history_config_capacity_risk", FormattableString.Invariant($"History planning scenario exceeds the size/disk budget: {ArchivedTags} tags, about {SamplesPerSecond:F0} samples/s, raw alone about {RawScenarioBytes / (1024d * 1024 * 1024):F0} GiB before 30% journal allowance. Assumes every scan changes, 100 bytes/numeric row, configured cadence and full cutoff month; NOT a forecast or bound. Deadband savings are unknown; subscriptions may arrive faster. Aggregates, backups and {UnmodelledDerivedTags} job-derived tags are excluded. Review cadence/deadband/retention and measure on target hardware; periodic mode does not cap changes."));
    }
}
