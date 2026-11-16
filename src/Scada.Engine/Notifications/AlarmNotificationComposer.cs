using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;

namespace Scada.Engine.Notifications;

/// <summary>
/// 报警外发正文与 JSON 载荷的组装。从 <see cref="AlarmNotificationDispatcher"/> 抽出：
/// 投递、重试、去重留在分发器，文本与时区换算只在这里，便于单独验证。
/// </summary>
public static class AlarmNotificationComposer
{
    // 外发正文用的上下文：设备/测点名、单位、规则上的原因与处置。历史正文只有 message+数值+区域，
    // 设备离线类报警（没有测点）在短信里完全没有主语。
    public sealed record AlarmContext(string? Device, string? Tag, string? Unit, string? Cause, string? Action)
    {
        public static readonly AlarmContext Empty = new(null, null, null, null, null);
    }

    public static async Task<AlarmContext> ContextAsync(ConfigDbContext db, AlarmEvent alarm, CancellationToken ct)
    {
        string? device = null, tag = null, unit = null;
        if (alarm.TagId is int tagId)
        {
            var point = await db.Tags.AsNoTracking().Where(x => x.Id == tagId)
                .Select(x => new { x.Name, x.Unit, x.DeviceId }).FirstOrDefaultAsync(ct);
            if (point is not null)
            {
                tag = point.Name;
                unit = point.Unit;
                if (point.DeviceId is int pointDevice)
                {
                    device = await db.Devices.AsNoTracking().Where(x => x.Id == pointDevice).Select(x => x.Name).FirstOrDefaultAsync(ct);
                }
            }
        }

        if (device is null && alarm.DeviceId is int deviceId)
        {
            device = await db.Devices.AsNoTracking().Where(x => x.Id == deviceId).Select(x => x.Name).FirstOrDefaultAsync(ct);
        }

        string? cause = null, advice = null;
        if (alarm.RuleId is int ruleId)
        {
            var rule = await db.AlarmRules.AsNoTracking().Where(x => x.Id == ruleId)
                .Select(x => new { x.Cause, x.Action }).FirstOrDefaultAsync(ct);
            cause = rule?.Cause;
            advice = rule?.Action;
        }

        return new AlarmContext(device, tag, unit, cause, advice);
    }

    public static NotificationMessage Compose(
        AlarmEvent alarm, string action, string routeName, IReadOnlyList<string> areaNames,
        AlarmContext context, string? shiftTimeZone, DateTimeOffset? now = null)
    {
        var level = alarm.Level switch { AlarmLevel.Critical => "严重", AlarmLevel.Warning => "警告", _ => "提示" };
        var verb = action switch
        {
            "raise" => "报警",
            "recover" => "恢复",
            "escalate" => "未确认升级",
            "suppress" => "抑制",
            "unsuppress" => "解除抑制",
            "close" => "关闭",
            "test" => "测试",
            _ => action
        };
        var time = ToShiftTime(DateTimeOffset.FromUnixTimeMilliseconds(alarm.StartTs), shiftTimeZone).ToString("yyyy-MM-dd HH:mm:ss");
        var area = areaNames.Count == 0 ? "-" : string.Join("、", areaNames);
        var lines = new List<string> { $"{verb}：{alarm.Message}", $"等级：{level}" };
        if (!string.IsNullOrWhiteSpace(context.Device)) lines.Add($"设备：{context.Device}");
        if (!string.IsNullOrWhiteSpace(context.Tag)) lines.Add($"测点：{context.Tag}");
        // 设备离线/网关类报警没有测点，Value 恒为 0，输出“数值：0”是伪信息。
        if (alarm.TagId is not null) lines.Add($"数值：{Format(alarm.Value, context.Unit)}");
        if (!string.IsNullOrWhiteSpace(context.Cause)) lines.Add($"原因：{context.Cause}");
        if (!string.IsNullOrWhiteSpace(context.Action)) lines.Add($"处置：{context.Action}");
        lines.Add($"开始：{time}");
        lines.Add($"区域：{area}");
        lines.Add($"报警号：#{alarm.Id}");
        var subject = $"[SCADA][{level}][{verb}] {alarm.Message}";
        var body = string.Join("\n", lines);
        var payload = new { type = "alarm", action, route = routeName, areas = areaNames, sentAt = now ?? DateTimeOffset.UtcNow, device = context.Device, tag = context.Tag, cause = context.Cause, advice = context.Action, alarm };
        return new(subject, body, payload);
    }

    private static string Format(double value, string? unit) =>
        string.IsNullOrWhiteSpace(unit) ? value.ToString("0.###") : $"{value:0.###} {unit.Trim()}";

    public static DateTimeOffset ToShiftTime(DateTimeOffset utc, string? shiftTimeZone)
    {
        var zone = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(shiftTimeZone))
        {
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(shiftTimeZone); }
            catch (Exception) { /* 时区无效时回退本机时区 */ }
        }
        return TimeZoneInfo.ConvertTime(utc, zone);
    }
}
