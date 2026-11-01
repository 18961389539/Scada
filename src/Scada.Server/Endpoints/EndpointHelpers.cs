using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public static class EndpointHelpers
{
    public static IResult BadRequest(string message) =>
        Results.BadRequest(new { message });

    // 端点把底层异常转成“暂不可用”前先落日志，避免服务端无迹可查。
    internal static void LogFailure(HttpContext http, string category, Exception exception, LogLevel level = LogLevel.Error) =>
        http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(category)
            .Log(level, exception, "Request failed {Method} {Path}", http.Request.Method, http.Request.Path);

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

        if (!Enum.IsDefined(rule.Type))
        {
            return "报警类型无效";
        }

        if (!Enum.IsDefined(rule.ActiveWhen))
        {
            return "位报警触发条件无效";
        }

        if (!Enum.IsDefined(rule.Level))
        {
            return "报警级别无效";
        }

        if (!Enum.IsDefined(rule.Category))
        {
            return "报警类别无效";
        }

        if (!Enum.IsDefined(rule.Consequence))
        {
            return "报警后果无效";
        }

        if (rule.ResponseTimeSec is < 0 or > 86400)
        {
            return "允许响应时间须在 0～86400 秒之间";
        }

        if (rule.AutoPriority && rule.ResponseTimeSec <= 0)
        {
            return "自动优先级需要填写允许响应时间";
        }

        // 位报警和旧的状态变化类型不使用阈值；其余类型必须有有效阈值。
        if (rule.Type is not (AlarmRuleType.StateChange or AlarmRuleType.Discrete) && double.IsNaN(rule.Threshold))
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

        // 只写“高限/状态变化”等于把类型名重复一遍，操作员拿不到任何信息；留空则由引擎自动生成含数值的文案。
        if (rule.Message is { Length: > 0 } message && AlarmRuleText.IsPlaceholderOnly(message))
        {
            return "报警消息不能只写报警类型，请说明现象与处置（可留空自动生成）";
        }

        if (rule.Message?.Length > AlarmMessageBuilder.MaxLength)
        {
            return $"报警消息不能超过 {AlarmMessageBuilder.MaxLength} 个字符";
        }

        if (rule.Cause?.Length > 200 || rule.Action?.Length > 200)
        {
            return "原因与处置各不能超过 200 个字符";
        }

        return null;
    }

    // 保存前检查测点类型与重复规则：位报警只接受 Bool；完全相同的规则（含消息与启用状态）视为重复。
    public static async Task<string?> ValidateAlarmRuleTargetAsync(ConfigDbContext db, AlarmRule rule, int? excludeRuleId, CancellationToken ct)
    {
        var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == rule.TagId, ct);
        if (tag is null)
        {
            return $"测点不存在：{rule.TagId}";
        }

        if (rule.Type == AlarmRuleType.Discrete && tag.DataType != TagDataType.Bool)
        {
            return "位报警只能用于 Bool 测点";
        }

        var level = rule.AutoPriority ? AlarmPriority.Compute(rule.Consequence, rule.ResponseTimeSec) : rule.Level;
        var duplicate = await db.AlarmRules.AsNoTracking().AnyAsync(x =>
            x.Id != (excludeRuleId ?? 0) &&
            x.TagId == rule.TagId &&
            x.Type == rule.Type &&
            x.Level == level &&
            x.Enabled == rule.Enabled &&
            x.Message == rule.Message &&
            (rule.Type != AlarmRuleType.Discrete || x.ActiveWhen == rule.ActiveWhen) &&
            (rule.Type == AlarmRuleType.StateChange || Math.Abs(x.Threshold - rule.Threshold) < 1e-9), ct);
        return duplicate ? "同一测点已存在相同的报警规则" : null;
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
