using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Data;

namespace Scada.Server.Services;

public sealed partial class GatewayCenterService
{
    private readonly ConcurrentDictionary<int, GatewayDeviceHealth> _deviceHealth = new();
    private static long SampleFreshness(Device device) => Math.Max(3000L, Math.Max(device.PollIntervalMs * 3L, device.PollIntervalMs + (long)device.TimeoutMs));
    private static bool Fresh(long ts, long now, long age) => ts > 0 && ts <= now + 1000 && now - ts <= age;
    private bool CurrentObservation(GatewayDeviceHealth state, AcquisitionGateway gateway, string revision) =>
        state.GatewayId == gateway.Id && state.SessionId == gateway.SessionId && state.ObservedRevision == revision
        && Fresh(state.ReceivedTs, Now, _settings.LeaseSeconds * 1000L) && state.ObservedTs <= Now + 1000;
    private bool Healthy(GatewayDeviceHealth state, Device device) => state.Online
        && Fresh(state.LastPollTs, Now, Math.Max(SampleFreshness(device), _settings.LeaseSeconds * 1000L));

    private async Task<GatewayDeviceHealth> HealthAsync(ConfigDbContext db, Device device, int gatewayId, string revision, CancellationToken ct)
    {
        var state = await db.GatewayDeviceHealth.FindAsync([device.Id], ct);
        if (state is null) { state = new() { DeviceId = device.Id }; db.GatewayDeviceHealth.Add(state); }
        if (state.GatewayId != gatewayId)
        {
            state.GatewayId = gatewayId; state.SessionId = null; state.ObservedRevision = null; state.ReceivedTs = state.ObservedTs = state.LastPollTs = 0;
            state.Online = false; state.Error = null; state.ExpectedRevision = "";
        }
        if (state.ExpectedRevision != revision)
        { state.ExpectedRevision = revision; state.MonitoringSinceTs = Now; state.OfflineSinceTs = null; }
        return state;
    }
    private async Task<GatewayDeviceHealth> ObserveAsync(ConfigDbContext db, Device device, AcquisitionGateway gateway, string revision,
        long observedTs, bool online, long lastPollTs, string? error, CancellationToken ct)
    {
        var state = await HealthAsync(db, device, gateway.Id, revision, ct);
        if (observedTs > Now + 1000) return state; // A future source clock cannot recover or poison subsequent valid evidence.
        if (state.SessionId == gateway.SessionId && state.ObservedRevision == revision
            && (observedTs < state.ObservedTs || observedTs == state.ObservedTs && !state.Online && online)) return state;
        state.SessionId = gateway.SessionId; state.ObservedRevision = revision; state.ObservedTs = observedTs;
        state.ReceivedTs = Now; state.LastPollTs = lastPollTs; state.Online = online;
        state.Error = error is null ? null : error[..Math.Min(1000, error.Length)]; return state;
    }
    private void RememberHealth(IEnumerable<GatewayDeviceHealth> states)
    { foreach (var state in states) _deviceHealth[state.DeviceId] = state.Clone(); }

    // Evaluate persisted evidence, never infer recovery from a process restart or an expired alarm ID.
    public async Task<int> ReconcileDeviceAlarmsAsync(CancellationToken ct)
    {
        var notifications = new List<(AlarmEvent Alarm, string Action)>();
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var devices = await db.Devices.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var counts = await db.Tags.GroupBy(x => x.DeviceId).Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
            var states = await db.GatewayDeviceHealth.ToListAsync(ct);
            var stateIds = states.Select(x => x.DeviceId).ToHashSet();
            var remoteIds = devices.Values.Where(x => x.GatewayId != null).Select(x => x.Id).ToArray();
            var active = await db.AlarmEvents.Where(x => x.DeviceId != null && x.RuleId == null && x.TagId == null && x.EndTs == null
                && (x.GatewayId != null || remoteIds.Contains(x.DeviceId.Value))).ToListAsync(ct);
            void Close(AlarmEvent alarm, string reason, bool recovered)
            { alarm.EndTs = Math.Max(Now, alarm.StartTs); alarm.EndReason = reason; notifications.Add((alarm, recovered ? "recover" : "close")); }
            foreach (var alarm in active)
            {
                devices.TryGetValue(alarm.DeviceId!.Value, out var device);
                if (device is null) Close(alarm, "设备已删除", false);
                else if (device.GatewayId != alarm.GatewayId) Close(alarm, "采集位置已变更", false);
                else if (!device.Enabled) Close(alarm, "设备采集已停用", false);
                else if (counts.GetValueOrDefault(device.Id) == 0) Close(alarm, "设备已无采集测点", false);
            }
            var activeByDevice = active.Where(x => x.EndTs is null).ToDictionary(x => x.DeviceId!.Value);
            var gateways = await db.Gateways.ToDictionaryAsync(x => x.Id, ct);
            foreach (var group in devices.Values.Where(x => x.Enabled && x.GatewayId != null && counts.GetValueOrDefault(x.Id) > 0).GroupBy(x => x.GatewayId!.Value))
            {
                var revision = (await BuildConfigurationAsync(db, group.Key, ct, recordRevision: false)).Revision;
                var gateway = gateways[group.Key]; Remember(gateway);
                foreach (var device in group)
                {
                    var state = await HealthAsync(db, device, gateway.Id, revision, ct);
                    if (stateIds.Add(state.DeviceId)) states.Add(state);
                    activeByDevice.TryGetValue(device.Id, out var alarm);
                    var connected = gateway.Enabled && gateway.SessionId != null && gateway.LeaseUntilTs > Now;
                    var ready = connected && gateway.AppliedRevision == revision;
                    var observed = ready && CurrentObservation(state, gateway, revision);
                    if (observed && Healthy(state, device))
                    { state.OfflineSinceTs = null; if (alarm is not null) Close(alarm, "当前网关已确认设备通信恢复", true); continue; }
                    // Explicit current failure is evidence immediately. Unknown startup/configuration state gets a grace interval.
                    var knownFailure = observed && !state.Online;
                    if (!knownFailure && Now - state.MonitoringSinceTs < _settings.DeviceStateGraceMs)
                    { state.OfflineSinceTs = null; continue; }
                    var since = knownFailure ? Math.Min(Now, Math.Max(state.MonitoringSinceTs, state.ObservedTs))
                        : !connected && gateway.LeaseUntilTs > 0 ? Math.Min(Now, Math.Max(state.MonitoringSinceTs, gateway.LeaseUntilTs)) : Now;
                    state.OfflineSinceTs ??= since;
                    if (alarm is not null || Now - state.OfflineSinceTs < _settings.DeviceOfflineDelayMs) continue;
                    var reason = !connected ? "网关未连接、已停用或租约已过期" : !ready ? "等待网关确认当前配置超时"
                        : knownFailure ? "网关报告设备通信故障或采集结果过期" : "缺少当前会话的新鲜设备状态";
                    alarm = new() { DeviceId = device.Id, GatewayId = gateway.Id, Level = AlarmLevel.Warning, StartTs = state.OfflineSinceTs.Value,
                        Message = $"设备离线：{device.Name}（网关 #{gateway.Id}）：{reason}" };
                    db.AlarmEvents.Add(alarm); notifications.Add((alarm, "raise"));
                }
            }
            var removed = states.Where(x => !devices.TryGetValue(x.DeviceId, out var device) || !device.Enabled || device.GatewayId != x.GatewayId || counts.GetValueOrDefault(x.DeviceId) == 0).ToArray();
            db.GatewayDeviceHealth.RemoveRange(removed);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            RememberHealth(states.Except(removed)); foreach (var state in removed) _deviceHealth.TryRemove(state.DeviceId, out _);
        }
        finally { _mutations.Release(); }
        foreach (var (alarm, action) in notifications)
            try { if (_alarmNotifier is not null) await _alarmNotifier.PublishAlarmAsync(alarm, action, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "网关设备报警已保存，实时推送失败"); }
        return notifications.Count;
    }
    private async Task MonitorDeviceAlarmsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ReconcileDeviceAlarmsAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "网关设备报警核对失败，保留现有状态后重试"); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
