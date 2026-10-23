using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Server.Endpoints;

public static class EndpointHelpers
{
    public static IResult BadRequest(string message) =>
        Results.BadRequest(new { message });

    public static long ParseTime(string? value, long fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (long.TryParse(value, out var ms))
        {
            return ms;
        }

        return DateTimeOffset.TryParse(value, out var parsed) ? parsed.ToUnixTimeMilliseconds() : fallback;
    }

    public static int[] ParseIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<int>();
        }

        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var id) ? id : 0)
            .Where(x => x > 0)
            .Distinct()
            .ToArray();
    }

    public static string? ValidateDevice(Device device) => ConfigurationValidator.Device(device);

    public static string? ValidateTag(Tag tag) => ConfigurationValidator.Tag(tag);

    public static string? ValidateAlarmRule(AlarmRule rule)
    {
        if (rule.TagId <= 0)
        {
            return "必须指定测点";
        }

        if (rule.Type != AlarmRuleType.StateChange && double.IsNaN(rule.Threshold))
        {
            return "阈值无效";
        }

        if (rule.DelaySec < 0)
        {
            return "报警延时不能为负数";
        }

        if (rule.RecoverDeadband < 0)
        {
            return "恢复死区不能为负数";
        }

        return null;
    }

    public static string FormatValue(double value) => value.ToString("0.###");

    public static Tag CloneWithId(Tag tag, int id) => new()
    {
        Id = id,
        DeviceId = tag.DeviceId,
        Name = tag.Name,
        Description = tag.Description,
        Area = tag.Area,
        Address = tag.Address,
        DbNumber = tag.DbNumber, BitOffset = tag.BitOffset,
        NodeId = tag.NodeId,
        DataType = tag.DataType,
        Scale = tag.Scale,
        Offset = tag.Offset,
        Unit = tag.Unit,
        Writable = tag.Writable,
        ArchiveMode = tag.ArchiveMode,
        Deadband = tag.Deadband,
        ArchivePeriodSec = tag.ArchivePeriodSec,
        MinValue = tag.MinValue,
        MaxValue = tag.MaxValue
    };

    public static TagValue QualityFailure(int tagId, long ts) =>
        new(tagId, 0, TagQuality.CommFailure, ts);
}
