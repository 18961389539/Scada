using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;

namespace Scada.Server.Mcp;

public sealed partial class ScadaMcpTools
{
    private const long StaleAfterMs = 5 * 60 * 1000;
    private const int MaxAuditValueChars = 1000;
    private const int ContextDeviceLimit = 100;
    private const int ContextAlarmLimit = 50;
    private const int ContextProblemTagLimit = 50;
    private const int ContextGatewayLimit = 10;

    [McpServerTool(Name = "query_audit_log", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read stored operation audit entries (user, action, target and outcome) newest first for a time range of at most 31 days. Optional exact userName and targetType, an action/result keyword and an outcome filter; offset up to 10000, at most 100 rows per page. Configuration change diffs (details) are intentionally omitted; hasDetails only marks their presence. Old/new values are capped at 1000 characters. Read-only; no write, delete or export.")]
    public async Task<McpAuditPage> QueryAuditLog(
        [Description("Inclusive start time in Unix milliseconds (UTC).")] long from,
        [Description("Inclusive end time in Unix milliseconds (UTC), at most 31 days after from.")] long to,
        CancellationToken cancellationToken,
        [Description("Optional exact user name, up to 100 characters.")] string? userName = null,
        [Description("Optional substring of the action or result, up to 100 characters.")] string? keyword = null,
        [Description("Optional exact target type, for example Tag or ConfigVersion, up to 50 characters.")] string? targetType = null,
        [Description("Optional outcome filter; true means the result starts with 成功.")] bool? success = null,
        [Description("Number of rows to skip, 0 to 10000.")] int offset = 0,
        [Description("Page size, 1 to 100.")] int limit = 50)
    {
        ValidateRange(from, to);
        ValidatePage(offset, limit);
        if (offset > 10000)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "offset must not exceed 10000; narrow the time range.");
        if (userName?.Length > 100 || keyword?.Length > 100 || targetType?.Length > 50)
            throw new McpToolException(McpErrorCodes.InvalidArgument,
                "userName and keyword must not exceed 100 characters; targetType must not exceed 50 characters.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var query = db.OperationLogs.AsNoTracking().Where(x => x.Ts >= from && x.Ts <= to);
        if (!string.IsNullOrWhiteSpace(userName))
        {
            var value = userName.Trim();
            query = query.Where(x => x.UserName == value);
        }
        if (!string.IsNullOrWhiteSpace(targetType))
        {
            var value = targetType.Trim();
            query = query.Where(x => x.TargetType == value);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var value = keyword.Trim();
            query = query.Where(x => x.Action.Contains(value) || x.Result.Contains(value));
        }
        if (success is true) query = query.Where(x => x.Result.StartsWith("成功"));
        else if (success is false) query = query.Where(x => !x.Result.StartsWith("成功"));
        var rows = await query.OrderByDescending(x => x.Id).Skip(offset).Take(limit + 1).ToListAsync(cancellationToken);
        var valuesCut = false;
        var items = new List<McpAuditEntry>(Math.Min(rows.Count, limit));
        foreach (var row in rows.Take(limit))
        {
            items.Add(new McpAuditEntry(row.Id, row.Ts, row.UserId, row.UserName, row.Action, row.TargetType,
                row.TargetId, row.TagId, Cap(row.OldValue), Cap(row.NewValue), row.Result, row.Details is not null));
        }
        var more = rows.Count > limit;
        var nextOffset = more && offset + limit <= 10000 ? offset + limit : (int?)null;
        return new(items, nextOffset, valuesCut || more && offset + limit > 10000, ScadaTime.NowMs());

        string? Cap(string? value)
        {
            if (value is not { Length: > MaxAuditValueChars }) return value;
            valuesCut = true;
            return value[..MaxAuditValueChars];
        }
    }

    [McpServerTool(Name = "get_operations_context", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read one device's or area's current operating context in one call: configuration, cached collector status, problem tags (no cached value, non-Good quality or older than 5 minutes), active alarms attributed to the scope and related gateways. Provide exactly one of deviceId or areaId. At most 100 devices, 50 alarms, 50 problem tags and 10 gateways; truncated is explicit. Read-only; no probe, reconnect, acknowledgement or write.")]
    public async Task<McpOperationsContext> GetOperationsContext(
        CancellationToken cancellationToken,
        [Description("Optional positive device ID. Omit when areaId is supplied.")] int? deviceId = null,
        [Description("Optional positive area ID. Omit when deviceId is supplied.")] int? areaId = null)
    {
        if ((deviceId is null) == (areaId is null))
            throw new McpToolException(McpErrorCodes.InvalidArgument, "Provide exactly one of deviceId or areaId.");
        if (deviceId is <= 0 || areaId is <= 0)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "deviceId and areaId must be positive.");
        var now = ScadaTime.NowMs();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        McpContextScope scope;
        List<ContextDeviceRow> devices;
        if (deviceId is int id)
        {
            var device = await db.Devices.AsNoTracking().Where(x => x.Id == id)
                .Select(x => new ContextDeviceRow(x.Id, x.Name, x.Enabled, x.GatewayId))
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
            var areaIdOfDevice = await db.Devices.AsNoTracking().Where(x => x.Id == id).Select(x => x.AreaId)
                .SingleAsync(cancellationToken);
            scope = new("device", device.Id, device.Name, areaIdOfDevice, null);
            devices = [device];
        }
        else
        {
            var area = await db.Areas.AsNoTracking().Where(x => x.Id == areaId)
                .Select(x => new { x.Id, x.Name, x.Description })
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new McpToolException(McpErrorCodes.NotFound, "Area does not exist.");
            scope = new("area", area.Id, area.Name, area.Id, area.Description);
            devices = await db.Devices.AsNoTracking().Where(x => x.AreaId == area.Id)
                .Select(x => new ContextDeviceRow(x.Id, x.Name, x.Enabled, x.GatewayId))
                .ToListAsync(cancellationToken);
        }

        var tagQuery = db.Tags.AsNoTracking();
        if (deviceId is int scopeDevice)
            tagQuery = tagQuery.Where(x => x.DeviceId == scopeDevice);
        else
            tagQuery = tagQuery.Where(x => (x.Kind != TagKind.Physical && x.AreaId == areaId)
                || (x.Kind == TagKind.Physical && db.Devices.Any(d => d.Id == x.DeviceId && d.AreaId == areaId)));
        var tags = await tagQuery.Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken);
        var tagNames = tags.ToDictionary(x => x.Id, x => x.Name);
        var values = cache.GetMany(tagNames.Keys.ToArray()).ToDictionary(x => x.TagId);
        var missing = tagNames.Keys.Where(x => !values.ContainsKey(x)).ToHashSet();
        var bad = values.Values.Where(x => x.Quality != TagQuality.Good).Select(x => x.TagId).ToHashSet();
        var staleBefore = now - StaleAfterMs;
        var stale = values.Values.Where(x => x.Ts < staleBefore).Select(x => x.TagId).ToHashSet();
        var problemIds = tagNames.Keys.OrderBy(x => x)
            .Where(x => missing.Contains(x) || bad.Contains(x) || stale.Contains(x)).ToArray();
        var samples = problemIds.Take(ContextProblemTagLimit).Select(tagId =>
        {
            var name = tagNames[tagId];
            if (!values.TryGetValue(tagId, out var value))
                return new McpContextTag(tagId, name, "Missing", null, null, null, null);
            return new McpContextTag(tagId, name, value.Quality.ToString(), value.Value, value.Ts, now - value.Ts, value.Error);
        }).ToArray();

        var deviceIds = devices.Select(x => x.Id).ToArray();
        var alarmTagIds = deviceId is null
            ? await db.Tags.AsNoTracking().Where(x => x.Kind != TagKind.Physical && x.AreaId == areaId)
                .Select(x => x.Id).ToArrayAsync(cancellationToken)
            : await db.Tags.AsNoTracking().Where(x => x.DeviceId == deviceId && x.Kind != TagKind.Physical)
                .Select(x => x.Id).ToArrayAsync(cancellationToken);
        var alarmQuery = db.AlarmEvents.AsNoTracking()
            .Where(x => x.EndTs == null && !x.Shelved && !x.Suppressed)
            .Where(x => (x.DeviceId != null && deviceIds.Contains(x.DeviceId.Value))
                || (x.DeviceId == null && x.TagId != null && alarmTagIds.Contains(x.TagId.Value)));
        var activeAlarmCount = await alarmQuery.CountAsync(cancellationToken);
        var unackAlarmCount = await alarmQuery.CountAsync(x => x.AckTs == null, cancellationToken);
        var alarmRows = await alarmQuery.OrderByDescending(x => x.Level).ThenByDescending(x => x.StartTs)
            .ThenByDescending(x => x.Id).Take(ContextAlarmLimit + 1).ToListAsync(cancellationToken);
        var access = new AccessContext(null, [new AccessGrant(Permission.View, null)], await accessModels.GetAsync(cancellationToken));
        var activeAlarms = alarmRows.Take(ContextAlarmLimit).Select(x => AlarmView(x, access)).ToArray();

        var gatewayIds = devices.Where(x => x.GatewayId != null).Select(x => x.GatewayId!.Value).Distinct().ToArray();
        var gatewayRows = (await gateways.ListAsync(cancellationToken))
            .Where(x => gatewayIds.Contains(x.Id)).OrderBy(x => x.Id).ToArray();
        var gatewayItems = gatewayRows.Take(ContextGatewayLimit)
            .Select(x => new McpContextGateway(x.Id, x.Name, x.Connected, x.LastSeenTs, x.Error)).ToArray();

        var statuses = collector.GetStatuses().ToDictionary(x => x.DeviceId);
        var deviceItems = devices.Take(ContextDeviceLimit)
            .Select(x => new McpContextDevice(x.Id, x.Name, x.Enabled, statuses.GetValueOrDefault(x.Id))).ToArray();
        var devicesTruncated = devices.Count > ContextDeviceLimit;
        var problemTagsTruncated = problemIds.Length > ContextProblemTagLimit;
        var alarmsTruncated = activeAlarmCount > ContextAlarmLimit;
        var gatewaysTruncated = gatewayRows.Length > ContextGatewayLimit;
        return new(scope, deviceItems, devicesTruncated, tagNames.Count, missing.Count, bad.Count, stale.Count,
            samples, problemTagsTruncated, activeAlarms, activeAlarmCount, unackAlarmCount, alarmsTruncated,
            gatewayItems, gatewaysTruncated, now,
            devicesTruncated || problemTagsTruncated || alarmsTruncated || gatewaysTruncated);
    }

    private sealed record ContextDeviceRow(int Id, string Name, bool Enabled, int? GatewayId);

    public sealed record McpAuditEntry(int Id, long Ts, int? UserId, string UserName, string Action, string? TargetType,
        string? TargetId, int? TagId, string? OldValue, string? NewValue, string Result, bool HasDetails);
    public sealed record McpAuditPage(IReadOnlyList<McpAuditEntry> Items, int? NextOffset, bool Truncated, long DataAsOf);
    public sealed record McpContextScope(string Kind, int Id, string Name, int? AreaId, string? Description);
    public sealed record McpContextDevice(int Id, string Name, bool Enabled, CollectorDeviceStatus? Status);
    public sealed record McpContextTag(int TagId, string Name, string Quality, double? Value, long? Ts, long? AgeMs, string? Error);
    public sealed record McpContextGateway(int Id, string Name, bool Connected, long LastSeenTs, string? Error);
    public sealed record McpOperationsContext(McpContextScope Scope, IReadOnlyList<McpContextDevice> Devices,
        bool DevicesTruncated, int TotalTags, int MissingTags, int BadQualityTags, int StaleTags,
        IReadOnlyList<McpContextTag> ProblemTags, bool ProblemTagsTruncated,
        IReadOnlyList<McpAlarmView> ActiveAlarms, int ActiveAlarmCount, int UnackAlarmCount, bool AlarmsTruncated,
        IReadOnlyList<McpContextGateway> Gateways, bool GatewaysTruncated, long DataAsOf, bool Truncated);
}
