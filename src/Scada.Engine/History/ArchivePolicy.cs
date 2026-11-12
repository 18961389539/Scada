using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.History;

public sealed record ArchiveState(long Ts, double Value, TagQuality Quality, string? Text = null);

public static class ArchivePolicy
{
    public static bool ShouldStore(Tag tag, TagValue current, ArchiveState? lastStored, long nowMs)
    {
        if (tag.ArchiveMode == ArchiveMode.None)
        {
            return false;
        }

        if (lastStored is null)
        {
            return true;
        }

        if (current.Quality != lastStored.Quality)
        {
            return true;
        }

        if (tag.DataType == TagDataType.Bool)
        {
            return current.Value != lastStored.Value;
        }

        if (tag.DataType == TagDataType.String)
        {
            var textChanged = !string.Equals(current.Text, lastStored.Text, StringComparison.Ordinal);
            var period = Math.Max(tag.ArchivePeriodSec, 1) * 1000L;
            return tag.ArchiveMode == ArchiveMode.Periodic ? textChanged || nowMs - lastStored.Ts >= period : textChanged;
        }

        var changed = Math.Abs(current.Value - lastStored.Value) > tag.Deadband;
        if (tag.ArchiveMode == ArchiveMode.OnChange)
        {
            return changed;
        }

        var periodMs = Math.Max(tag.ArchivePeriodSec, 1) * 1000L;
        return changed || nowMs - lastStored.Ts >= periodMs;
    }
}
