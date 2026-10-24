using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Server.Auth;

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

        if (rule.DelaySec > 3600 || rule.OffDelaySec is < 0 or > 3600)
        {
            return "触发/恢复延时须在 0～3600 秒之间";
        }

        if (rule.SuppressMode == AlarmSuppressMode.None ? rule.SuppressTagId is not null : rule.SuppressTagId is null)
        {
            return "条件抑制需同时指定抑制方式和条件测点";
        }

        if (rule.SuppressTagId == rule.TagId)
        {
            return "条件测点不能是报警测点本身";
        }

        return null;
    }

    // 显式请求的测点必须全部有权限，否则整体 403（读请求不写审计）。
    public static async Task<IResult?> RequireTagsAsync(HttpContext http, IEnumerable<int> tagIds, Permission permission, string action)
    {
        var access = await http.GetAccessAsync();
        var denied = tagIds.Where(id => !access.CanTag(permission, id)).ToArray();
        return denied.Length == 0 ? null : await Audit.ForbidAsync(http, action, $"无权访问测点：{string.Join(",", denied.Take(20))}", "Tag", string.Join(",", denied.Take(20)));
    }

    public static async Task<IResult?> RequireDeviceAsync(HttpContext http, int deviceId, Permission permission, string action)
    {
        var access = await http.GetAccessAsync();
        return access.CanDevice(permission, deviceId) ? null : await Audit.ForbidAsync(http, action, "无权操作该设备所在区域", "Device", deviceId);
    }

    public static async Task<IResult?> RequireAreaAsync(HttpContext http, int areaId, Permission permission, string action)
    {
        var access = await http.GetAccessAsync();
        return access.Has(permission, areaId) ? null : await Audit.ForbidAsync(http, action, $"无权操作区域 #{areaId}", "Area", areaId);
    }

    public static bool CanAlarm(AccessContext access, Permission permission, AlarmEvent alarm) =>
        alarm.TagId is int tagId ? access.CanTag(permission, tagId)
        : alarm.DeviceId is int deviceId ? access.CanDevice(permission, deviceId)
        : access.HasAny(permission);

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
