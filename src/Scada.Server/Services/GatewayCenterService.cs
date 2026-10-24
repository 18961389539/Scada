using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.History;
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Server.Services;

public sealed class GatewayRequestException(int statusCode, string message, string code = "request_rejected") : Exception(message)
{ public int StatusCode { get; } = statusCode; public string Code { get; } = code; }
public sealed record GatewayCaller(int Id, string KeyStamp);
public sealed record GatewayAdminInfo(int Id, string Name, bool Enabled, bool Connected, long LastSeenTs, long LeaseUntilTs,
    string? AgentVersion, string? AppliedRevision, int DeviceCount, long PendingBatches, long PendingBytes, long? OldestPendingTs,
    long InboxBatches, long InboxBytes, string? Error);

public sealed partial class GatewayCenterService : BackgroundService
{
    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly IDeviceCredentials _credentials;
    private readonly IConfigSnapshot _config;
    private readonly IRealtimeCache _cache;
    private readonly IRealtimeBus _bus;
    private readonly IGatewayHistoryWriter _history;
    private readonly CollectorEngine _local;
    private readonly GatewaySettings _settings;
    private readonly ILogger<GatewayCenterService> _logger;
    private readonly TimeProvider _clock;
    private readonly IAlarmNotifier? _alarmNotifier;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly ConcurrentDictionary<int, (Guid? SessionId, long Until, bool Enabled, string? Applied)> _leases = new();
    private readonly ConcurrentDictionary<int, (int Version, string Hash)> _revisions = new();
    private readonly ConcurrentDictionary<int, CollectorDeviceStatus> _deviceStatuses = new();

    public GatewayCenterService(IDbContextFactory<ConfigDbContext> factory, IDeviceCredentials credentials, IConfigSnapshot config,
        IRealtimeCache cache, IRealtimeBus bus, IGatewayHistoryWriter history, CollectorEngine local, IOptions<GatewaySettings> settings,
        ILogger<GatewayCenterService> logger, TimeProvider? clock = null, IAlarmNotifier? alarmNotifier = null)
    { _factory = factory; _credentials = credentials; _config = config; _cache = cache; _bus = bus; _history = history; _local = local; _settings = settings.Value; _logger = logger; _clock = clock ?? TimeProvider.System; _alarmNotifier = alarmNotifier; }
    private long Now => _clock.GetUtcNow().ToUnixTimeMilliseconds();
    public static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<IReadOnlyList<GatewayAdminInfo>> ListAsync(CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var gateways = await db.Gateways.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct);
        var counts = await db.Devices.Where(x => x.GatewayId != null).GroupBy(x => x.GatewayId!.Value).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var inbox = await db.GatewayInbox.GroupBy(x => x.GatewayId).Select(g => new { Id = g.Key, Count = g.LongCount(), Bytes = g.Sum(x => (long)x.PayloadBytes) }).ToDictionaryAsync(x => x.Id, ct);
        var failures = (await db.GatewayInbox.Where(x => x.ProcessingError != null).Select(x => new { x.GatewayId, x.ProcessingError }).ToListAsync(ct)).GroupBy(x => x.GatewayId).ToDictionary(x => x.Key, x => x.First().ProcessingError);
        return gateways.Select(x => new GatewayAdminInfo(x.Id, x.Name, x.Enabled, x.Enabled && x.SessionId != null && x.LeaseUntilTs > Now,
            x.LastSeenTs, x.LeaseUntilTs, x.AgentVersion, x.AppliedRevision, counts.GetValueOrDefault(x.Id), x.PendingBatches, x.PendingBytes,
            x.OldestPendingTs, inbox.GetValueOrDefault(x.Id)?.Count ?? 0, inbox.GetValueOrDefault(x.Id)?.Bytes ?? 0, failures.GetValueOrDefault(x.Id) ?? x.LastError)).ToList();
    }

    public async Task<(int Id, string Token)> CreateAsync(string name, int userId, string userName, CancellationToken ct)
    {
        name = Name(name); var token = NewToken(); await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.Gateways.AnyAsync(x => x.Name == name, ct)) throw new GatewayRequestException(409, "网关名称已存在");
            var gateway = new AcquisitionGateway { Name = name, TokenHash = TokenHash(token) }; db.Gateways.Add(gateway);
            Audit(db, userId, userName, $"创建采集网关：{name}"); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return (gateway.Id, token);
        }
        finally { _mutations.Release(); }
    }

    public async Task<string?> ChangeAsync(int id, string? name, bool? enabled, bool rotateKey, bool release, int userId, string userName, CancellationToken ct)
    {
        if (name is not null) name = Name(name);
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var gateway = await db.Gateways.SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new GatewayRequestException(404, "网关不存在");
            if (name is not null && await db.Gateways.AnyAsync(x => x.Id != id && x.Name == name, ct)) throw new GatewayRequestException(409, "网关名称已存在");
            if (name is not null) gateway.Name = name; if (enabled is not null) gateway.Enabled = enabled.Value;
            string? token = null;
            if (rotateKey) { token = NewToken(); gateway.TokenHash = TokenHash(token); }
            if (rotateKey || release || enabled == false)
            { gateway.SessionId = null; gateway.InstanceId = null; gateway.ActiveStreamId = null; gateway.LeaseUntilTs = 0; gateway.AppliedRevision = null; }
            Audit(db, userId, userName, $"{(rotateKey ? "轮换网关密钥" : release ? "释放网关会话" : "修改采集网关")}：#{id} {gateway.Name}");
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); Remember(gateway); return token;
        }
        finally { _mutations.Release(); }
    }

    public async Task AssignAsync(int deviceId, int? gatewayId, int userId, string userName, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var device = await db.Devices.SingleOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new GatewayRequestException(404, "设备不存在");
            if (device.GatewayId == gatewayId) return;
            if (device.Enabled || !_local.IsDeviceQuiescent(deviceId)) throw new GatewayRequestException(409, "请先停用设备并等待原采集任务完全退出，再切换采集位置");
            if (gatewayId is not null && !await db.Gateways.AnyAsync(x => x.Id == gatewayId && x.Enabled, ct)) throw new GatewayRequestException(409, "目标网关不存在或已停用");
            if (device.GatewayId is int oldId)
            {
                var old = await db.Gateways.SingleAsync(x => x.Id == oldId, ct);
                var applied = old.AppliedRevision is null ? null : await db.GatewayConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.GatewayId == oldId && x.Revision == old.AppliedRevision, ct);
                var metadata = applied is null ? null : JsonSerializer.Deserialize<GatewayConfiguration>(applied.SnapshotJson, GatewayProtocol.Json);
                if (old.LastSeenTs < device.DisabledSinceTs || metadata?.Devices.Any(x => x.Device.Id == deviceId && !x.Device.Enabled) != true)
                    throw new GatewayRequestException(409, "原网关尚未确认停用配置，暂不能迁移设备");
            }
            device.GatewayId = gatewayId; Audit(db, userId, userName, $"切换设备采集位置：#{deviceId} → {(gatewayId is null ? "本地" : $"网关 #{gatewayId}")}");
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            _deviceStatuses.TryRemove(deviceId, out _);
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewaySession> HelloAsync(GatewayCaller caller, GatewayHello hello, CancellationToken ct)
    {
        if (hello.Version != GatewayProtocol.Version || hello.InstanceId == Guid.Empty || hello.StreamId == Guid.Empty || string.IsNullOrWhiteSpace(hello.AgentVersion) || hello.AgentVersion.Length > 64
            || !GatewayProtocol.ValidCapabilities(hello.Capabilities))
            throw new GatewayRequestException(400, "网关版本、进程或数据流标识无效");
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); var gateway = await AuthorizedAsync(db, caller, ct);
            var now = Now;
            if (gateway.SessionId is not null && gateway.LeaseUntilTs > now)
            {
                if (gateway.InstanceId != hello.InstanceId || gateway.ActiveStreamId != hello.StreamId) throw new GatewayRequestException(409, "此网关已有活动进程，请等待租约到期或由管理员释放会话", "session_in_use");
            }
            else { gateway.SessionId = Guid.NewGuid(); gateway.AppliedRevision = null; }
            gateway.InstanceId = hello.InstanceId; gateway.ActiveStreamId = hello.StreamId; gateway.AgentVersion = hello.AgentVersion;
            gateway.CapabilitiesJson = JsonSerializer.Serialize((hello.Capabilities ?? []).Distinct().OrderBy(x => x, StringComparer.Ordinal), GatewayProtocol.Json);
            gateway.LastSeenTs = now; gateway.LeaseUntilTs = now + _settings.LeaseSeconds * 1000L;
            var stream = await db.GatewayStreams.FindAsync([caller.Id, hello.StreamId], ct);
            if (stream is null) { stream = new GatewayDataStream { GatewayId = caller.Id, StreamId = hello.StreamId }; db.GatewayStreams.Add(stream); }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); Remember(gateway);
            return new(GatewayProtocol.Version, gateway.SessionId!.Value, now, gateway.LeaseUntilTs, stream.ReceivedThrough, [GatewayProtocol.EngineeringCapability]);
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewayConfiguration> ConfigurationAsync(GatewayCaller caller, Guid sessionId, CancellationToken ct)
        => await ConfigurationUpdateAsync(caller, sessionId, null, ct) ?? throw new InvalidOperationException("配置响应为空");

    public async Task<GatewayConfiguration?> ConfigurationUpdateAsync(GatewayCaller caller, Guid sessionId, string? knownRevision, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            await SessionAsync(db, caller, sessionId, ct); var configuration = await BuildConfigurationAsync(db, caller.Id, ct);
            if (configuration.Revision == knownRevision)
            { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }
            var devices = await db.Devices.AsNoTracking().Where(x => x.GatewayId == caller.Id).OrderBy(x => x.Id).ToListAsync(ct);
            var secrets = devices.ToDictionary(x => x.Id, x => x.Protocol != ProtocolType.OpcUa || x.OpcPasswordProtected is null ? null : _credentials.Unprotect(x.OpcPasswordProtected));
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return configuration with { Devices = configuration.Devices.Select(x => x with { OpcPassword = secrets[x.Device.Id] }).ToArray() };
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewaySession> HeartbeatAsync(GatewayCaller caller, Guid sessionId, GatewayHeartbeat heartbeat, CancellationToken ct)
    {
        if (heartbeat.PendingBatches < 0 || heartbeat.PendingBytes < 0 || heartbeat.OldestPendingTs is < 1 || heartbeat.Error?.Length > 1000
            || heartbeat.AppliedRevision is not null && !GatewayProtocol.IsRevision(heartbeat.AppliedRevision) || heartbeat.Devices?.Count > 5000 || !GatewayProtocol.ValidCapabilities(heartbeat.Capabilities))
            throw new GatewayRequestException(400, "网关心跳参数无效");
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); var gateway = await SessionAsync(db, caller, sessionId, ct);
            if (heartbeat.AppliedRevision is not null && !await db.GatewayConfigurations.AnyAsync(x => x.GatewayId == caller.Id && x.Revision == heartbeat.AppliedRevision, ct))
                throw new GatewayRequestException(409, "配置版本尚未下发给此网关");
            var owned = await db.Devices.AsNoTracking().Where(x => x.GatewayId == caller.Id).ToDictionaryAsync(x => x.Id, ct);
            if (heartbeat.Devices is not null && (heartbeat.Devices.Any(x => x is null || !owned.ContainsKey(x.DeviceId) || x.LastPollTs < 0 || x.LastPollTs > Now + 120000 || x.StatusTs is < 1 || x.StatusTs > Now + 120000 || !double.IsFinite(x.LastCycleMs) || x.LastCycleMs < 0 || x.ReconnectAttempts < 0 || x.CommFailures < 0 || x.Error?.Length > 1000)
                || heartbeat.Devices.Select(x => x.DeviceId).Distinct().Count() != heartbeat.Devices.Count)) throw new GatewayRequestException(400, "心跳包含无效或不属于此网关的设备状态");
            gateway.AppliedRevision = heartbeat.AppliedRevision; gateway.PendingBatches = heartbeat.PendingBatches; gateway.PendingBytes = heartbeat.PendingBytes;
            if (heartbeat.Capabilities is not null) gateway.CapabilitiesJson = JsonSerializer.Serialize(heartbeat.Capabilities.Distinct().OrderBy(x => x, StringComparer.Ordinal), GatewayProtocol.Json);
            gateway.OldestPendingTs = heartbeat.OldestPendingTs; gateway.LastError = heartbeat.Error; gateway.LastSeenTs = Now; gateway.LeaseUntilTs = Now + _settings.LeaseSeconds * 1000L;
            var stream = await db.GatewayStreams.FindAsync([caller.Id, gateway.ActiveStreamId!.Value], ct);
            var health = new List<GatewayDeviceHealth>();
            if (heartbeat.AppliedRevision is not null && heartbeat.Devices?.Count > 0)
            {
                var current = await BuildConfigurationAsync(db, caller.Id, ct, recordRevision: false);
                if (heartbeat.AppliedRevision == current.Revision)
                {
                    var populated = current.Tags.Select(x => x.DeviceId).ToHashSet();
                    foreach (var status in heartbeat.Devices.Where(x => owned[x.DeviceId].Enabled && populated.Contains(x.DeviceId)))
                    {
                        var device = owned[status.DeviceId]; var stale = status.Online && !Fresh(status.LastPollTs, Now, SampleFreshness(device));
                        health.Add(await ObserveAsync(db, device, gateway, current.Revision, stale ? Now : status.StatusTs ?? Now,
                            status.Online && !stale, status.LastPollTs, stale ? "网关报告的采集结果已过期" : status.Error, ct));
                    }
                }
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); Remember(gateway);
            RememberHealth(health);
            foreach (var status in heartbeat.Devices ?? []) _deviceStatuses[status.DeviceId] = status with { Name = owned[status.DeviceId].Name };
            return new(GatewayProtocol.Version, sessionId, Now, gateway.LeaseUntilTs, stream!.ReceivedThrough, [GatewayProtocol.EngineeringCapability]);
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewayAcknowledgement> GoodbyeAsync(GatewayCaller caller, Guid sessionId, CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var gateway = await SessionAsync(db, caller, sessionId, ct); var stream = gateway.ActiveStreamId!.Value;
            gateway.SessionId = null; gateway.InstanceId = null; gateway.ActiveStreamId = null; gateway.LeaseUntilTs = 0;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); Remember(gateway); return new(stream, 0);
        }
        finally { _mutations.Release(); }
    }

    public async Task<GatewayAcknowledgement> AcceptAsync(GatewayCaller caller, Guid sessionId, string payload, string expectedHash, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(payload) > GatewaySettings.MaxPacketBytes) throw new GatewayRequestException(413, "网关数据包过大");
        var actualHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        if (!GatewayProtocol.IsRevision(expectedHash) || !string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new GatewayRequestException(400, "网关数据摘要不匹配");
        var packet = JsonSerializer.Deserialize<GatewayTelemetry>(payload, GatewayProtocol.Json) ?? throw new GatewayRequestException(400, "网关数据为空"); ValidatePacket(packet);
        await _mutations.WaitAsync(ct);
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); var gateway = await SessionAsync(db, caller, sessionId, ct);
            if (gateway.ActiveStreamId != packet.StreamId) throw new GatewayRequestException(409, "数据流不属于当前会话", "session_expired");
            var stream = await db.GatewayStreams.FindAsync([caller.Id, packet.StreamId], ct) ?? throw new GatewayRequestException(409, "数据流尚未注册");
            if (packet.Sequence <= stream.ReceivedThrough)
            {
                if (packet.Sequence == stream.ReceivedThrough && stream.LastPayloadHash != actualHash) throw new GatewayRequestException(409, "相同数据序号的内容已变化");
                return new(packet.StreamId, packet.Sequence);
            }
            if (packet.Sequence != stream.ReceivedThrough + 1) throw new GatewayRequestException(409, $"数据包乱序，期望序号 {stream.ReceivedThrough + 1}");
            var revision = await db.GatewayConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.GatewayId == caller.Id && x.Revision == packet.ConfigurationRevision, ct)
                ?? throw new GatewayRequestException(409, "此网关未取得该配置版本");
            var configuration = JsonSerializer.Deserialize<GatewayConfiguration>(revision.SnapshotJson, GatewayProtocol.Json)!;
            var tags = configuration.Tags.Where(x => x.DeviceId == packet.DeviceId).Select(x => x.Id).ToHashSet();
            if (!configuration.Devices.Any(x => x.Device.Id == packet.DeviceId) || packet.Values.Any(x => !tags.Contains(x.TagId))) throw new GatewayRequestException(403, "数据包含未分配给此网关的设备或测点");
            var bytes = Encoding.UTF8.GetByteCount(payload); var pending = await db.GatewayInbox.SumAsync(x => (long?)x.PayloadBytes, ct) ?? 0;
            if (bytes > _settings.MaxInboxPendingBytes - pending) throw new GatewayRequestException(429, "中心持久收件箱已满，请保留网关数据并稍后重试");
            db.GatewayInbox.Add(new() { GatewayId = caller.Id, StreamId = packet.StreamId, Sequence = packet.Sequence, SessionId = sessionId, DeviceId = packet.DeviceId,
                Revision = packet.ConfigurationRevision, PayloadJson = payload, PayloadHash = actualHash, PayloadBytes = bytes, ReceivedTs = Now });
            stream.ReceivedThrough = packet.Sequence; stream.LastPayloadHash = actualHash;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); Remember(gateway); return new(packet.StreamId, packet.Sequence);
        }
        finally { _mutations.Release(); }
    }

    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        await _mutations.WaitAsync(ct);
        GatewayInboxBatch? selected = null;
        try
        {
            await using var db = await OpenAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
            var batch = await (from inbox in db.GatewayInbox join progress in db.GatewayStreams
                on new { inbox.GatewayId, inbox.StreamId } equals new { progress.GatewayId, progress.StreamId }
                where inbox.Sequence == progress.ProcessedThrough + 1 && inbox.NextAttemptTs <= Now orderby inbox.ReceivedTs, inbox.Sequence select inbox).FirstOrDefaultAsync(ct);
            if (batch is null) return false;
            selected = batch;
            var stream = await db.GatewayStreams.FindAsync([batch.GatewayId, batch.StreamId], ct) ?? throw new InvalidDataException("网关收件箱数据流缺失");
            if (batch.Sequence != stream.ProcessedThrough + 1) throw new InvalidDataException("网关收件箱处理序号不连续");
            if (TokenHash(batch.PayloadJson) != batch.PayloadHash) throw new InvalidDataException("网关收件箱数据校验失败");
            var packet = JsonSerializer.Deserialize<GatewayTelemetry>(batch.PayloadJson, GatewayProtocol.Json)!;
            var revision = await db.GatewayConfigurations.AsNoTracking().SingleAsync(x => x.GatewayId == batch.GatewayId && x.Revision == batch.Revision, ct);
            var metadata = JsonSerializer.Deserialize<GatewayConfiguration>(revision.SnapshotJson, GatewayProtocol.Json)!;
            var tags = metadata.Tags.ToDictionary(x => x.Id);
            var cursors = await db.GatewayArchiveCursors.Where(x => x.GatewayId == batch.GatewayId && x.StreamId == batch.StreamId).ToDictionaryAsync(x => x.TagId, ct);
            var history = new List<HistoryValue>();
            foreach (var value in packet.Values)
            {
                var tag = tags[value.TagId]; cursors.TryGetValue(value.TagId, out var cursor);
                var last = cursor is null ? null : new ArchiveState(cursor.Ts, cursor.Value, cursor.Quality);
                if (tag.ArchiveMode == ArchiveMode.None || cursor is not null && value.Ts == cursor.Ts
                    || cursor is not null && value.Ts > cursor.Ts && !ArchivePolicy.ShouldStore(tag, value, last, value.Ts)) continue;
                history.Add(new(value.TagId, value.Ts, value.Value, value.Quality));
                if (cursor is null) { cursor = new() { GatewayId = batch.GatewayId, StreamId = batch.StreamId, TagId = value.TagId }; db.GatewayArchiveCursors.Add(cursor); cursors[value.TagId] = cursor; }
                if (value.Ts >= cursor.Ts) { cursor.Ts = value.Ts; cursor.Value = value.Value; cursor.Quality = value.Quality; }
            }
            await _history.WriteGatewayHistoryAsync(history, ct);
            var gateway = await db.Gateways.SingleAsync(x => x.Id == batch.GatewayId, ct);
            var current = await BuildConfigurationAsync(db, batch.GatewayId, ct, recordRevision: false);
            GatewayDeviceHealth? health = null;
            if (gateway.Enabled && gateway.LeaseUntilTs > Now && gateway.SessionId == batch.SessionId && gateway.AppliedRevision == current.Revision && packet.ConfigurationRevision == current.Revision)
            {
                var device = current.Devices.FirstOrDefault(x => x.Device.Id == packet.DeviceId)?.Device;
                if (device?.Enabled == true && _config.TryGetDevice(device.Id, out var observed) && observed.GatewayId == gateway.Id && observed.Enabled)
                {
                    var fresh = packet.Values.Where(x => Fresh(x.Ts, Now, SampleFreshness(device))).ToArray();
                    if (fresh.Length > 0) health = await ObserveAsync(db, device, gateway, current.Revision, fresh.Max(x => x.Ts),
                        fresh.Any(x => x.Quality != TagQuality.CommFailure), fresh.Max(x => x.Ts), fresh.FirstOrDefault(x => x.Error is not null)?.Error, ct);
                }
            }
            stream.ProcessedThrough = batch.Sequence; db.GatewayInbox.Remove(batch);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); Remember(gateway);
            if (health is not null) RememberHealth([health]);
            if (gateway.Enabled && gateway.LeaseUntilTs > Now && gateway.SessionId == batch.SessionId && gateway.AppliedRevision == current.Revision && packet.ConfigurationRevision == current.Revision)
            {
                var device = current.Devices.FirstOrDefault(x => x.Device.Id == packet.DeviceId)?.Device;
                if (device?.Enabled == true && _config.TryGetDevice(device.Id, out var observed) && observed.GatewayId == batch.GatewayId && observed.Enabled)
                {
                    var cutoff = Math.Max(3000L, Math.Max(device.PollIntervalMs * 3L, device.PollIntervalMs + (long)device.TimeoutMs));
                    foreach (var point in packet.Values)
                    {
                        var old = _cache.Get(point.TagId);
                        if (point.Ts > Now + 1000 || Now - point.Ts > cutoff || old is not null && point.Ts <= old.Ts) continue;
                        _cache.Set(point); if (old is null || old.Value != point.Value || old.Quality != point.Quality) _bus.Publish(point);
                    }
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (selected is not null)
            {
                try
                {
                    await using var retry = await OpenAsync(ct); var next = Now + 1000; var reason = $"数据流 {selected.StreamId} 的序号 {selected.Sequence} 处理失败，保留待重试";
                    await retry.GatewayInbox.Where(x => x.GatewayId == selected.GatewayId && x.StreamId == selected.StreamId && x.Sequence == selected.Sequence)
                        .ExecuteUpdateAsync(set => set.SetProperty(x => x.NextAttemptTs, next).SetProperty(x => x.ProcessingError, reason), ct);
                }
                catch (Exception retryError) when (retryError is not OperationCanceledException) { _logger.LogWarning(retryError, "无法记录网关重试进度，原始数据仍保留"); }
            }
            throw;
        }
        finally { _mutations.Release(); }
    }

    public IReadOnlyList<CollectorDeviceStatus> GetStatuses()
    {
        var now = Now;
        return _config.Devices.Where(x => x.Enabled && x.GatewayId != null).Select(device =>
        {
            var tags = _config.Tags.Where(x => x.DeviceId == device.Id).ToList();
            _leases.TryGetValue(device.GatewayId!.Value, out var lease); _revisions.TryGetValue(device.GatewayId.Value, out var revision);
            _deviceStatuses.TryGetValue(device.Id, out var reported);
            _deviceHealth.TryGetValue(device.Id, out var health);
            var connected = lease.Enabled && lease.SessionId != null && lease.Until > now;
            var ready = connected && revision.Version == _config.Version && lease.Applied == revision.Hash;
            var online = ready && health?.GatewayId == device.GatewayId && health.SessionId == lease.SessionId && health.ObservedRevision == revision.Hash
                && Fresh(health.ReceivedTs, now, _settings.LeaseSeconds * 1000L) && health.ObservedTs <= now + 1000 && Healthy(health, device);
            return new CollectorDeviceStatus(device.Id, device.Name, online, online ? health?.Error : !connected ? "网关未连接或租约已过期" : !ready ? "等待网关确认当前配置" : health?.Error ?? "等待当前会话的新鲜设备状态",
                health?.LastPollTs ?? 0, reported?.LastCycleMs ?? 0, tags.Count, reported?.ReconnectAttempts ?? 0, reported?.CommFailures ?? 0, health?.ObservedTs);
        }).Where(x => x.TagCount > 0).ToList();
    }

    private async Task<GatewayConfiguration> BuildConfigurationAsync(ConfigDbContext db, int gatewayId, CancellationToken ct, bool recordRevision = true)
    {
        var devices = await db.Devices.AsNoTracking().Where(x => x.GatewayId == gatewayId).OrderBy(x => x.Id).ToListAsync(ct); var ids = devices.Select(x => x.Id).ToArray();
        var tags = await db.Tags.AsNoTracking().Where(x => ids.Contains(x.DeviceId)).OrderBy(x => x.Id).ToListAsync(ct);
        var publicDevices = devices.Select(x => { var clone = x.Clone(); clone.OpcPasswordProtected = null; return new GatewayDeviceConfiguration(clone, null); }).ToArray();
        var configuration = new GatewayConfiguration("", publicDevices, tags);
        var fingerprint = JsonSerializer.Serialize(configuration, GatewayProtocol.Json) + string.Join('|', devices.Select(x => $"{x.Id}:{x.OpcPasswordProtected}"));
        var revision = TokenHash(fingerprint); configuration = configuration with { Revision = revision };
        if (recordRevision && !await db.GatewayConfigurations.AnyAsync(x => x.GatewayId == gatewayId && x.Revision == revision, ct))
            db.GatewayConfigurations.Add(new() { GatewayId = gatewayId, Revision = revision, SnapshotJson = JsonSerializer.Serialize(configuration, GatewayProtocol.Json), CreatedTs = Now });
        _revisions[gatewayId] = (_config.Version, revision); return configuration;
    }
    private async Task<ConfigDbContext> OpenAsync(CancellationToken ct)
    {
        var db = await _factory.CreateDbContextAsync(ct);
        try { await db.Database.OpenConnectionAsync(ct); await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL", ct); return db; }
        catch { await db.DisposeAsync(); throw; }
    }
    private static async Task<AcquisitionGateway> AuthorizedAsync(ConfigDbContext db, GatewayCaller caller, CancellationToken ct)
    {
        var gateway = await db.Gateways.SingleOrDefaultAsync(x => x.Id == caller.Id, ct);
        if (gateway is null || !gateway.Enabled || gateway.TokenHash != caller.KeyStamp) throw new GatewayRequestException(401, "网关身份已失效", "identity_revoked"); return gateway;
    }
    private async Task<AcquisitionGateway> SessionAsync(ConfigDbContext db, GatewayCaller caller, Guid sessionId, CancellationToken ct)
    {
        var gateway = await AuthorizedAsync(db, caller, ct);
        if (sessionId == Guid.Empty || gateway.SessionId != sessionId || gateway.LeaseUntilTs <= Now) throw new GatewayRequestException(409, "网关会话已失效，请重新握手", "session_expired"); return gateway;
    }
    private void Remember(AcquisitionGateway gateway) => _leases[gateway.Id] = (gateway.SessionId, gateway.LeaseUntilTs, gateway.Enabled, gateway.AppliedRevision);
    private void ValidatePacket(GatewayTelemetry packet)
    {
        if (packet.Version != GatewayProtocol.Version || packet.StreamId == Guid.Empty || packet.Sequence < 1 || packet.DeviceId < 1 || !GatewayProtocol.IsRevision(packet.ConfigurationRevision)
            || packet.CreatedTs < 1 || packet.CreatedTs > Now + 120000 || packet.Values is null || packet.Values.Count is < 1 or > GatewayProtocol.MaxValuesPerBatch
            || packet.Values.Any(x => x is null || x.TagId < 1 || !double.IsFinite(x.Value) || !Enum.IsDefined(x.Quality) || x.Ts < 1 || x.Ts > Now + 120000 || x.SourceTs is < 0 || x.Error?.Length > 1000)
            || packet.Values.Select(x => x.TagId).Distinct().Count() != packet.Values.Count) throw new GatewayRequestException(400, "网关数据包无效");
    }
    private static string Name(string? name) => name?.Trim() is string value && value.Length is > 0 and <= 100 ? value : throw new GatewayRequestException(400, "网关名称须为 1～100 个字符");
    private void Audit(ConfigDbContext db, int id, string name, string action) => db.OperationLogs.Add(new() { UserId = id, UserName = name, Ts = Now, Action = action, Result = "成功" });

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(ProcessInboxAsync(stoppingToken), MonitorDeviceAlarmsAsync(stoppingToken));
    private async Task ProcessInboxAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { if (!await ProcessOneAsync(stoppingToken)) await Task.Delay(200, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "网关持久收件箱处理失败，数据保留待重试"); try { await Task.Delay(100, stoppingToken); } catch (OperationCanceledException) { break; } }
        }
    }
}
