using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public static class EndpointHelpers
{
    private static readonly JsonSerializerOptions CopyOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // 审计留痕用的实体快照：按运行时类型序列化，枚举转字符串、放宽中文转义。
    public static JsonElement JsonCopy<T>(T value) => JsonSerializer.SerializeToElement(value, value!.GetType(), CopyOptions);

    public static IResult BadRequest(string message) =>
        Results.BadRequest(new { message });

    // 列表仍返回数组。满页时用响应头告诉界面还有更早的，以及下一页从哪个编号接着取。
    public static void MarkPage(HttpContext http, bool more, long? next)
    {
        http.Response.Headers["X-Scada-More"] = more ? "1" : "0";
        if (more && next is long cursor)
            http.Response.Headers["X-Scada-Next"] = cursor.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

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

    public static string? ValidateDevice(Device device, Scada.Core.Options.ScadaSettings? settings = null, Scada.Core.Interfaces.IProtocolDriverAvailability? drivers = null) => ConfigurationValidator.Device(device, settings, drivers);

    public static string? ValidateTag(Tag tag) => ConfigurationValidator.Tag(tag);

    // 报警规则的字段映射与校验实现已移到 AlarmRuleCommandService（写入的唯一落地点）。
    // 保留这两个转发是因为 MCP 配置工具与工程导入仍按旧入口调用；三处都迁移后可以删掉。
    public static string? ValidateAlarmRule(AlarmRule rule) => AlarmRuleCommandService.Validate(rule);

    public static Task<string?> ValidateAlarmRuleTargetAsync(ConfigDbContext db, AlarmRule rule, int? excludeRuleId, CancellationToken ct)
        => AlarmRuleCommandService.ValidateTargetAsync(db, rule, excludeRuleId, ct);

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
}
