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

    public static string? ValidateDevice(Device device)
    {
        if (string.IsNullOrWhiteSpace(device.Name))
        {
            return "设备名称不能为空";
        }

        if (string.IsNullOrWhiteSpace(device.Host))
        {
            return "设备地址不能为空";
        }

        if (device.Port is < 1 or > 65535)
        {
            return "端口必须在 1 ~ 65535 之间";
        }

        if (device.PollIntervalMs < 100)
        {
            return "采集周期不能小于 100 ms";
        }

        if (device.TimeoutMs < 100)
        {
            return "超时时间不能小于 100 ms";
        }

        return null;
    }

    public static string? ValidateTag(Tag tag)
    {
        if (tag.DeviceId <= 0)
        {
            return "必须指定设备";
        }

        if (string.IsNullOrWhiteSpace(tag.Name))
        {
            return "测点名称不能为空";
        }

        if (tag.Address < 0 || tag.Address + tag.RegisterCount - 1 > 65535)
        {
            return "地址超出 Modbus 范围";
        }

        if (tag.Area is TagArea.Coil or TagArea.DiscreteInput && tag.DataType != TagDataType.Bool)
        {
            return "线圈/离散输入区域只能使用 Bool 类型";
        }

        if (tag.Writable && tag.Area is TagArea.DiscreteInput or TagArea.InputRegister)
        {
            return "只读区域不能设置为可写";
        }

        if (tag.Deadband < 0)
        {
            return "死区不能为负数";
        }

        if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset) || tag.Writable && tag.Scale == 0)
            return "倍率或偏移无效，可写测点的倍率不能为零";

        if (tag.ArchiveMode == ArchiveMode.Periodic && tag.ArchivePeriodSec < 1)
        {
            return "周期存储的周期必须大于 0";
        }

        if (tag.MinValue is double min && tag.MaxValue is double max && min > max)
        {
            return "最小值不能大于最大值";
        }

        return null;
    }

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
