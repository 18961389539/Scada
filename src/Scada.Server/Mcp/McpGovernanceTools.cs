using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Engine.Maintenance;
using Scada.Server.Services;
using static Scada.Server.Mcp.McpArgs;

namespace Scada.Server.Mcp;

// 运行态治理与排障：健康快照、告警汇总/时间线/规则、网关概览、区域目录、审计日志、
// 单设备/单区域的操作上下文。全部只读，不探测、不重连、不改状态。
[McpServerToolType]
public sealed class McpGovernanceTools(
    AccessModelProvider accessModels,
    IDbContextFactory<ConfigDbContext> factory,
    IRealtimeCache cache,
    ICollectorControl collector,
    AlarmService alarms,
    AlarmShelfService alarmShelf,
    AlarmKpiService alarmKpi,
    AlarmFloodGuard alarmFloodGuard,
    GatewayCenterService gateways,
    HealthSnapshotService health,
    McpQueryBudget queryBudget)
{
    private const long StaleAfterMs = 5 * 60 * 1000;
    private const int MaxAuditValueChars = 1000;
    private const int ContextDeviceLimit = 100;
    private const int ContextAlarmLimit = 50;
    private const int ContextProblemTagLimit = 50;
    private const int ContextGatewayLimit = 10;

    [McpServerTool(Name = "query_health", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read the existing system health snapshot: status, warnings, device online counts, bus/backlog, history capacity and query limits, spool, disk/storage and HA role. At most 50 offline devices are listed; truncated reports truncation. Does not probe, reconnect or run diagnostics.")]
    public McpHealth QueryHealth()
    {
        var snapshot = health.Snapshot();
        var offline = snapshot.Devices.Where(x => !x.Online).OrderBy(x => x.DeviceId).Take(50)
            .Select(x => new McpOfflineDevice(x.DeviceId, x.Name, x.Error, x.LastPollTs, x.ReconnectAttempts, x.CommFailures))
            .ToArray();
        return new(snapshot.Status, snapshot.Warnings, snapshot.Ha, snapshot.Environment, snapshot.ServerTime,
            snapshot.UptimeSeconds, snapshot.HostUptimeSeconds, snapshot.OnlineDevices, snapshot.TotalDevices,
            offline, snapshot.ConfiguredTags, snapshot.BusBacklog, snapshot.BusDropped, snapshot.HistoryCapacity,
            snapshot.HistoryQueries, snapshot.HistorySpool, snapshot.AlarmWebhook, snapshot.AlarmNotifications,
            snapshot.Disk, snapshot.Storage, snapshot.ServerTime.ToUnixTimeMilliseconds(),
            snapshot.Devices.Count(x => !x.Online) > offline.Length);
    }

    [McpServerTool(Name = "get_alarm_summary", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read current live alarm state: ISA-18.2 state counts, shelved and out-of-service rule counts, the 10-minute alarm rate and per-area runtime flood-guard state including first-out and suppressed counts. Read-only; no acknowledgement, shelving, suppression or rule changes.")]
    public Task<McpAlarmSummary> GetAlarmSummary(CancellationToken cancellationToken) =>
        queryBudget.RunAsync(async ct =>
        {
            var counts = await alarms.CountStatesAsync(new AlarmQueryFilter(), ct);
            var ackableUnack = await alarms.CountAsync(null, null, cancellationToken: ct, liveOnly: true,
                filter: new AlarmQueryFilter { State = AlarmListState.Unack });
            await alarmShelf.EnsureLoadedAsync(ct);
            await using var db = await factory.CreateDbContextAsync(ct);
            var rules = await db.AlarmRules.AsNoTracking().Select(x => new { x.Id, x.OutOfService }).ToListAsync(ct);
            var ruleIds = rules.Select(x => x.Id).ToHashSet();
            var flood = await alarmKpi.FloodAsync(_ => true, null, ct);
            var now = ScadaTime.NowMs();
            var areaNames = await db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var areas = alarmFloodGuard.Snapshot(now).Select(x => new McpFloodArea(x.AreaId,
                x.AreaId is int id ? areaNames.GetValueOrDefault(id) : null, x.Count, x.Threshold, x.ExitThreshold,
                x.InFlood, x.WindowStartTs, x.WindowEndTs, x.FloodStartTs, x.Peak, x.FirstOutEventId, x.SuppressedCount))
                .ToArray();
            return new McpAlarmSummary(counts.Unack, counts.Acked, counts.RtnUnack, counts.Shelved, counts.Suppressed,
                counts.Chattering, ackableUnack, alarmShelf.Active.Count(x => ruleIds.Contains(x.RuleId)),
                rules.Count(x => x.OutOfService), flood,
                new McpFloodGuard(alarmFloodGuard.AutoSuppress, alarmFloodGuard.FirstOutEnabled,
                    alarmFloodGuard.SuppressMaxLevel, areas.Any(x => x.InFlood), areas),
                now, false);
        }, cancellationToken);

    [McpServerTool(Name = "list_alarm_rules", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List configured alarm rules with thresholds, delays, levels, suppress conditions and out-of-service state. Optional tagId/enabled/outOfService filters; ascending ID cursor, at most 100 rows. Read-only; no rule, shelve or out-of-service changes.")]
    public async Task<McpCursorPage<McpAlarmRule>> ListAlarmRules(CancellationToken cancellationToken,
        int? tagId = null, bool? enabled = null, bool? outOfService = null, int afterId = 0, int limit = 50)
    {
        ValidatePage(afterId, limit);
        if (tagId is <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "tagId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (tagId is int id && !await db.Tags.AnyAsync(x => x.Id == id, cancellationToken))
            throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
        var query = db.AlarmRules.AsNoTracking().Where(x => x.Id > afterId);
        if (tagId is not null) query = query.Where(x => x.TagId == tagId);
        if (enabled is not null) query = query.Where(x => x.Enabled == enabled);
        if (outOfService is not null) query = query.Where(x => x.OutOfService == outOfService);
        var rows = await query.OrderBy(x => x.Id).Take(limit + 1)
            .Select(x => new McpAlarmRule(x.Id, x.Key, x.TagId, x.Type, x.Threshold, x.Level, x.DelaySec,
                x.RecoverDeadband, x.Enabled, x.Message, x.OffDelaySec, x.SuppressTagId, x.SuppressMode,
                x.OutOfService, x.OutOfServiceReason))
            .ToListAsync(cancellationToken);
        return new(rows.Take(limit).ToArray(), rows.Count > limit ? rows[limit - 1].Id : null, ScadaTime.NowMs(), false);
    }

    [McpServerTool(Name = "get_alarm_timeline", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read one alarm event with its stored acknowledgement/note timeline and notification delivery log, at most 100 activities and 100 notifications each. Truncation flags are explicit. Read-only; does not acknowledge, shelve, suppress or resend notifications.")]
    public async Task<McpAlarmTimeline> GetAlarmTimeline(int alarmId, CancellationToken cancellationToken)
    {
        if (alarmId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "alarmId must be positive.");
        var timeline = await alarms.TimelineAsync(alarmId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Alarm does not exist.");
        var access = new AccessContext(null, [new AccessGrant(Permission.View, null)], await accessModels.GetAsync(cancellationToken));
        var activitiesTruncated = timeline.Activities.Count > 100;
        var notificationsTruncated = timeline.Notifications.Count > 100;
        return new McpAlarmTimeline(AlarmView(timeline.Alarm, access),
            timeline.Activities.Take(100).Select(x => new McpAlarmActivity(x.Id, x.Ts, x.User, x.Action, x.Note)).ToArray(),
            timeline.Notifications.Take(100).Select(x => new McpAlarmNotification(x.Id, x.RouteId, x.ChannelId, x.Action, x.Ts, x.Success, x.Error)).ToArray(),
            activitiesTruncated, notificationsTruncated, ScadaTime.NowMs(), activitiesTruncated || notificationsTruncated);
    }

    [McpServerTool(Name = "get_gateway_overview", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Read configured acquisition gateways with lease/connection state, device counts, queue and dead-letter sizes, revisions and reported errors. At most 100 gateways; tokens and credentials are never returned. Does not reconnect, send commands or run engineering work.")]
    public async Task<McpGatewayOverview> GetGatewayOverview(CancellationToken cancellationToken)
    {
        var all = await gateways.ListAsync(cancellationToken);
        var items = all.Take(100).Select(x => new McpGateway(x.Id, x.Name, x.Enabled, x.Connected, x.LastSeenTs,
            x.LeaseUntilTs, x.AgentVersion, x.AppliedRevision, x.CurrentRevision, x.ConfigurationConfirmed,
            x.DeviceCount, x.EnabledDeviceCount, x.PendingBatches, x.PendingBytes, x.OldestPendingTs,
            x.InboxBatches, x.InboxBytes, x.DeadLetterBatches, x.DeadLetterBytes, x.Capabilities,
            x.Error, x.InboxError)).ToArray();
        return new(items, all.Count > 100, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "list_areas", ReadOnly = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("List configured areas with device counts, tag counts and the default-area flag. Physical tags are counted in their device's area; other tags count under their explicit area only. At most 100 areas; truncated reports truncation.")]
    public Task<McpAreaList> ListAreas(CancellationToken cancellationToken) =>
        McpAreaCatalog.ListAsync(factory, cancellationToken);

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
            .Select(x => new McpDeviceRow(x.Id, x.Name, x.Enabled, statuses.GetValueOrDefault(x.Id))).ToArray();
        var devicesTruncated = devices.Count > ContextDeviceLimit;
        var problemTagsTruncated = problemIds.Length > ContextProblemTagLimit;
        var alarmsTruncated = activeAlarmCount > ContextAlarmLimit;
        var gatewaysTruncated = gatewayRows.Length > ContextGatewayLimit;
        return new(scope, deviceItems, devicesTruncated, tagNames.Count, missing.Count, bad.Count, stale.Count,
            samples, problemTagsTruncated, activeAlarms, activeAlarmCount, unackAlarmCount, alarmsTruncated,
            gatewayItems, gatewaysTruncated, now,
            devicesTruncated || problemTagsTruncated || alarmsTruncated || gatewaysTruncated);
    }

    private static McpAlarmView AlarmView(AlarmEvent x, AccessContext access)
    {
        int[] areas = x.TagId is int tagId && access.Model.TagAreas.TryGetValue(tagId, out var tagAreas) ? tagAreas
            : x.DeviceId is int deviceId && access.Model.DeviceAreas.TryGetValue(deviceId, out var area) ? [area]
            : [];
        return new(x.Id, x.TagId, x.DeviceId, x.GatewayId, x.RuleId, x.Level, x.Value, x.StartTs, x.EndTs,
            x.EndReason, x.AckUser, x.AckTs, x.Message, x.Shelved, x.Suppressed, x.SuppressReason, x.Chattering,
            x.FirstOut, AlarmStates.Of(x), areas);
    }

    private sealed record ContextDeviceRow(int Id, string Name, bool Enabled, int? GatewayId);

    public sealed record McpOfflineDevice(int DeviceId, string Name, string? Error, long LastPollTs,
        int ReconnectAttempts, long CommFailures);
    public sealed record McpHealth(string Status, IReadOnlyList<SystemWarning> Warnings, HealthHa? Ha,
        string Environment, DateTimeOffset ServerTime, long UptimeSeconds, long HostUptimeSeconds,
        int OnlineDevices, int TotalDevices, IReadOnlyList<McpOfflineDevice> OfflineDevices,
        int ConfiguredTags, int BusBacklog, long BusDropped, HistoryCapacity? HistoryCapacity,
        HealthHistoryQueries? HistoryQueries, HealthSpool HistorySpool, HealthWebhook AlarmWebhook,
        HealthNotifications? AlarmNotifications, HealthDisk Disk, HealthStorage Storage,
        long DataAsOf, bool Truncated);
    public sealed record McpAlarmSummary(int Unack, int Acked, int RtnUnack, int Shelved, int Suppressed, int Chattering,
        int AckableUnack, int ShelvedRules, int OutOfServiceRules, AlarmFloodStatus Flood, McpFloodGuard FloodGuard,
        long DataAsOf, bool Truncated);
    public sealed record McpFloodGuard(bool AutoSuppress, bool FirstOut, AlarmLevel SuppressMaxLevel, bool AnyInFlood,
        IReadOnlyList<McpFloodArea> Areas);
    public sealed record McpFloodArea(int? AreaId, string? AreaName, int Count, int Threshold, int ExitThreshold,
        bool InFlood, long WindowStartTs, long WindowEndTs, long? FloodStartTs, int Peak, int? FirstOutEventId, int SuppressedCount);
    public sealed record McpAlarmRule(int Id, string Key, int TagId, AlarmRuleType Type, double Threshold, AlarmLevel Level,
        int DelaySec, double RecoverDeadband, bool Enabled, string? Message, int OffDelaySec, int? SuppressTagId,
        AlarmSuppressMode SuppressMode, bool OutOfService, string? OutOfServiceReason);
    public sealed record McpAlarmActivity(long Id, long Ts, string? User, string Action, string? Note);
    public sealed record McpAlarmNotification(long Id, int RouteId, int ChannelId, string Action, long Ts, bool Success, string? Error);
    public sealed record McpAlarmTimeline(McpAlarmView Alarm, IReadOnlyList<McpAlarmActivity> Activities,
        IReadOnlyList<McpAlarmNotification> Notifications, bool ActivitiesTruncated, bool NotificationsTruncated,
        long DataAsOf, bool Truncated);
    public sealed record McpAlarmView(int Id, int? TagId, int? DeviceId, int? GatewayId, int? RuleId, AlarmLevel Level,
        double Value, long StartTs, long? EndTs, string? EndReason, string? AckUser, long? AckTs, string Message,
        bool Shelved, bool Suppressed, string? SuppressReason, bool Chattering, bool FirstOut, string State, int[] Areas);
    public sealed record McpGatewayOverview(IReadOnlyList<McpGateway> Items, bool Truncated, long DataAsOf);
    public sealed record McpGateway(int Id, string Name, bool Enabled, bool Connected, long LastSeenTs, long LeaseUntilTs,
        string? AgentVersion, string? AppliedRevision, string? CurrentRevision, bool ConfigurationConfirmed,
        int DeviceCount, int EnabledDeviceCount, long PendingBatches, long PendingBytes, long? OldestPendingTs,
        long InboxBatches, long InboxBytes, long DeadLetterBatches, long DeadLetterBytes,
        IReadOnlyList<string> Capabilities, string? Error, string? InboxError);
    public sealed record McpAuditEntry(int Id, long Ts, int? UserId, string UserName, string Action, string? TargetType,
        string? TargetId, int? TagId, string? OldValue, string? NewValue, string Result, bool HasDetails);
    public sealed record McpAuditPage(IReadOnlyList<McpAuditEntry> Items, int? NextOffset, bool Truncated, long DataAsOf);
    public sealed record McpContextScope(string Kind, int Id, string Name, int? AreaId, string? Description);
    public sealed record McpContextTag(int TagId, string Name, string Quality, double? Value, long? Ts, long? AgeMs, string? Error);
    public sealed record McpContextGateway(int Id, string Name, bool Connected, long LastSeenTs, string? Error);
    public sealed record McpOperationsContext(McpContextScope Scope, IReadOnlyList<McpDeviceRow> Devices,
        bool DevicesTruncated, int TotalTags, int MissingTags, int BadQualityTags, int StaleTags,
        IReadOnlyList<McpContextTag> ProblemTags, bool ProblemTagsTruncated,
        IReadOnlyList<McpAlarmView> ActiveAlarms, int ActiveAlarmCount, int UnackAlarmCount, bool AlarmsTruncated,
        IReadOnlyList<McpContextGateway> Gateways, bool GatewaysTruncated, long DataAsOf, bool Truncated);
}
