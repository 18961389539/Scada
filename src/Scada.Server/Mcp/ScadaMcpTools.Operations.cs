using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Engine.Alarms;
using Scada.Engine.Maintenance;
using Scada.Server.Services;

namespace Scada.Server.Mcp;

public sealed partial class ScadaMcpTools
{
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
    public async Task<McpAreaList> ListAreas(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var areas = await db.Areas.AsNoTracking().OrderBy(x => x.Id).Take(101).ToListAsync(cancellationToken);
        var deviceCounts = await db.Devices.AsNoTracking().GroupBy(x => x.AreaId)
            .Select(g => new { AreaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AreaId, x => x.Count, cancellationToken);
        var physicalCounts = await (from t in db.Tags.AsNoTracking()
                                    join d in db.Devices.AsNoTracking() on t.DeviceId equals (int?)d.Id
                                    where t.Kind == TagKind.Physical
                                    group d by d.AreaId into g
                                    select new { AreaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AreaId, x => x.Count, cancellationToken);
        var explicitCounts = await db.Tags.AsNoTracking()
            .Where(x => x.Kind != TagKind.Physical && x.AreaId != null)
            .GroupBy(x => x.AreaId!.Value)
            .Select(g => new { AreaId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.AreaId, x => x.Count, cancellationToken);
        var items = areas.Take(100).Select(x => new McpArea(x.Id, x.Name, x.Description,
            deviceCounts.GetValueOrDefault(x.Id),
            physicalCounts.GetValueOrDefault(x.Id) + explicitCounts.GetValueOrDefault(x.Id),
            x.Id == Area.DefaultId)).ToArray();
        return new(items, areas.Count > 100, ScadaTime.NowMs());
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
    public sealed record McpArea(int Id, string Name, string? Description, int DeviceCount, int TagCount, bool IsDefault);
    public sealed record McpAreaList(IReadOnlyList<McpArea> Items, bool Truncated, long DataAsOf);
}
