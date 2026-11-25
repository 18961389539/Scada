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

    /// <summary>
    /// 周期归档只保存采样时间仍在有效期内的原值，禁止把冻结样本改写成当前时间。
    /// 同一采样（相同时间戳）不重复落库。
    /// </summary>
    public static bool TryStorePeriodic(Tag tag, TagValue current, ArchiveState? lastStored, long nowMs, Device? device, out HistoryValue sample)
    {
        sample = default!;
        if (tag.ArchiveMode != ArchiveMode.Periodic) return false;
        if (TagFreshness.IsStale(current, nowMs, tag, device)) return false;
        if (lastStored is not null && lastStored.Ts == current.Ts) return false;
        if (!ShouldStore(tag, current, lastStored, nowMs)) return false;
        sample = new HistoryValue(tag.Id, current.Ts, current.Value, current.Quality, current.Text);
        return true;
    }
}
