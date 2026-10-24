using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Maintenance;

namespace Scada.Server.Ha;

public sealed record HaEvent(long Ts, string Kind, string Message, long Term, string? Actor = null);
public sealed record HaStatus(bool Enabled, string NodeId, string Role, long Term, long Generation, string? SelfUrl, string? PeerUrl, HaPeerStatus? Peer, long PeerSeenTs,
    string? Witness, HaLease? Lease, string? WitnessError, bool AutoFailover, long PromotedTs, long ConfigSyncTs, long HistorySyncTs, long HistoryRowsBehind,
    long ConfigApplied, long HistoryRowsApplied, string? ReplicationError, long Switchovers, IReadOnlyList<HaEvent> Events);

// 主备协调：心跳 + 仲裁租约（带任期）防双主；只有持有有效租约的节点是主机。
// 主机续租失败且本地租约期限已到 → 自我隔离降为备机；备机仅在租约过期（加时钟偏差余量）后才能接管。
public sealed class HaCoordinator : BackgroundService, INodeRole
{
    private readonly HaOptions _options;
    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly HaReplication _replication;
    private readonly ILogger<HaCoordinator> _logger;
    private readonly IHaWitness? _witness;
    private readonly HaPeerClient? _peerClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(int, Guid), bool> _gapStreams = new();
    private readonly LinkedList<HaEvent> _events = new();
    private readonly string _stateDir;
    private HaNodeState _state = new();
    private volatile bool _primary;
    private long _generation, _promotedTs, _peerSeenTs, _switchovers, _localLeaseDeadline, _configDue, _historyDue;
    private readonly long _startedTick = Environment.TickCount64;
    private HaPeerStatus? _peer;
    private HaLease? _lease;
    private string? _witnessError;
    private bool _initialized;

    private readonly LivenessRegistry? _liveness;

    public HaCoordinator(IOptions<HaOptions> options, IOptions<ScadaSettings> settings, IDbContextFactory<ConfigDbContext> factory, HaReplication replication, ILogger<HaCoordinator> logger, LivenessRegistry? liveness = null)
    {
        _liveness = liveness;
        _options = options.Value; _options.Validate();
        _factory = factory; _replication = replication; _logger = logger;
        _stateDir = Path.GetFullPath(_options.StateDir ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Value.ConfigDbPath))!, "ha"));
        if (!_options.Enabled) { _primary = true; return; }
        Directory.CreateDirectory(_stateDir);
        _state = LoadState();
        if (!string.IsNullOrWhiteSpace(_options.WitnessPath)) _witness = new FileHaWitness(Path.GetFullPath(_options.WitnessPath));
        _peerClient = new HaPeerClient(_options);
        foreach (var line in ReadEventLines()) _events.AddLast(line);
    }

    // 测试替换仲裁实现用。
    public IHaWitness? Witness => _witness;
    public bool Enabled => _options.Enabled;
    public bool IsActive => _primary;
    public long Generation => Interlocked.Read(ref _generation);
    public string StandbyMessage => $"本节点（{_options.NodeId}）为备机，不采集、不下发控制" + (string.IsNullOrEmpty(_peer?.SelfUrl) ? "" : $"；主机：{_peer!.SelfUrl}");
    public string? PrimaryUrl => _primary ? _options.SelfUrl : _peer?.Role == "Primary" ? _peer.SelfUrl ?? _options.PeerUrl : null;
    public string StateDir => _stateDir;
    public string NodeId => _options.NodeId;

    public bool ConsumeFailoverGap(int gatewayId, Guid streamId) => _options.Enabled && _primary && Interlocked.Read(ref _promotedTs) > 0 && _gapStreams.TryAdd((gatewayId, streamId), true);

    public HaPeerStatus Self() => new(_options.NodeId, _primary ? "Primary" : "Standby", _state.Term, _options.SelfUrl, ScadaTime.NowMs());

    public HaStatus Status()
    {
        HaEvent[] events; lock (_events) events = _events.Reverse().Take(50).ToArray();
        return new(_options.Enabled, _options.Enabled ? _options.NodeId : "standalone", _options.Enabled ? (_primary ? "Primary" : "Standby") : "Standalone", _state.Term, Generation,
            _options.SelfUrl, _options.Enabled ? _options.PeerUrl : null, _peer, _peerSeenTs, _witness?.Name, _lease, _witnessError, _options.AutoFailover && _witness is not null,
            _promotedTs, _replication.LastConfigSyncTs, _replication.LastHistorySyncTs, _replication.HistoryRowsBehind, _replication.ConfigApplied, _replication.HistoryRowsApplied,
            _replication.LastError, Interlocked.Read(ref _switchovers), events);
    }

    public IEnumerable<SystemWarning> Warnings()
    {
        if (!_options.Enabled) yield break;
        var now = ScadaTime.NowMs();
        if (_peer is null) yield return new("ha_peer_down", $"主备对端 {_options.PeerUrl} 不可达" + (_primary ? "，当前无热备" : ""));
        if (_witnessError is not null) yield return new("ha_witness_unreachable", $"仲裁不可用：{_witnessError}");
        if (!_primary && _peer?.Role == "Primary" && _replication.LastError is { } error) yield return new("ha_replication_failed", $"主备复制失败：{error}");
        if (!_primary && _peer?.Role == "Primary" && _replication.LastConfigSyncTs > 0 && now - _replication.LastConfigSyncTs > Math.Max(_options.ConfigSyncMs * 6, 60000))
            yield return new("ha_replication_stalled", "备机配置复制已超过 1 分钟未成功");
        if (!_primary && _peer is { Role: not "Primary" }) yield return new("ha_no_primary", "主备两节点都不是主机");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        Record("start", $"节点 {_options.NodeId} 启动为备机，等待选主（仲裁：{_witness?.Name ?? "无，仅手动切换"}）");
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.HeartbeatMs));
        do
        {
            _liveness?.Beat("ha");
            try
            {
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    await TickAsync(stoppingToken);
                    // 复制在同一把锁内进行：接管与复制互斥，避免接管后又被旧快照覆盖。
                    if (!_primary && _peer?.Role == "Primary") await ReplicateAsync(false, stoppingToken);
                }
                finally { _gate.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "主备协调异常"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // 正常停机时主动释放租约，备机无需等到租约过期即可接管。
        if (_options.Enabled && _primary && _witness is not null)
        {
            try
            {
                var term = _state.Term;
                await _witness.UpdateAsync(l => l is not null && l.Holder == _options.NodeId && l.Term == term ? l with { ExpiresTs = 0 } : l, cancellationToken);
                _primary = false; Interlocked.Increment(ref _generation);
                Record("stop", "服务停止，已释放主机租约");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "停止时释放租约失败"); }
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var peer = await _peerClient!.StatusAsync(ct);
        if (peer is not null) { _peer = peer; _peerSeenTs = ScadaTime.NowMs(); }
        else if (ScadaTime.NowMs() - _peerSeenTs > _options.LeaseMs) _peer = null;
        if (_witness is not null) await WitnessTickAsync(peer, ct);
        else await PeerOnlyTickAsync(peer, ct);
        _initialized = true;
    }

    private async Task WitnessTickAsync(HaPeerStatus? peer, CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        var me = _options.NodeId;
        if (_primary)
        {
            var term = _state.Term;
            try
            {
                var lease = await _witness!.UpdateAsync(l => l is not null && l.Holder == me && l.Term == term && l.HandoverTo is null ? l with { ExpiresTs = now + _options.LeaseMs } : l, ct);
                _lease = lease; _witnessError = null;
                if (lease is not null && lease.Holder == me && lease.Term == term && lease.HandoverTo is null) _localLeaseDeadline = Environment.TickCount64 + _options.LeaseMs - _options.ClockSkewMs;
                else await DemoteAsync($"仲裁租约已由 {lease?.Holder ?? "无"}（任期 {lease?.Term}）持有，本机降为备机", null, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _witnessError = ex.Message;
                if (Environment.TickCount64 >= _localLeaseDeadline) await DemoteAsync($"无法续租仲裁（{ex.Message}），租约到期，自我隔离为备机", null, ct);
            }
            return;
        }

        HaLease? current;
        try { current = await _witness!.ReadAsync(ct); _lease = current; _witnessError = null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _witnessError = ex.Message; return; }

        string? reason = null;
        if (current is null)
        {
            if (_options.PreferPrimary || Environment.TickCount64 - _startedTick > _options.LeaseMs) reason = "初始选主";
        }
        else if (current.Holder == me && current.HandoverTo is null && current.ExpiresTs > now) reason = "重启后恢复（租约仍由本节点持有）";
        else if (current.HandoverTo == me) { if (now > current.ExpiresTs + _options.LeaseMs) reason = "交接未完成，按交接目标接管"; }
        else if (current.HandoverTo is not null && now <= current.ExpiresTs + _options.LeaseMs * 2) reason = null; // 正在交接给对端
        else if (now > current.ExpiresTs + _options.ClockSkewMs)
        {
            if (current.ExpiresTs == 0) reason = "主机正常停机，接管";
            else if (_options.AutoFailover) reason = $"主机 {current.Holder} 租约已过期（自动切换）";
        }
        if (reason is null || peer?.Role == "Primary") return; // 对端仍自认为主机时等它自我隔离
        await AcquireAndPromoteAsync(reason, null, null, ct);
    }

    private async Task PeerOnlyTickAsync(HaPeerStatus? peer, CancellationToken ct)
    {
        if (!_initialized && !_primary && _options.PreferPrimary && peer?.Role != "Primary")
        {
            await PromoteAsync(Math.Max(_state.Term, peer?.Term ?? 0) + 1, "启动选主（无仲裁，按配置为主机）", null, ct);
            return;
        }
        // 网络恢复后发现双主：任期低者降级，任期相同按节点名决定，保证只有一方继续。
        if (_primary && peer?.Role == "Primary" && (peer.Term > _state.Term || peer.Term == _state.Term && string.CompareOrdinal(peer.NodeId, _options.NodeId) < 0))
            await DemoteAsync($"检测到双主，对端 {peer.NodeId} 任期 {peer.Term} 优先，本机降为备机", null, ct);
    }

    private async Task<bool> AcquireAndPromoteAsync(string reason, int? userId, string? actor, CancellationToken ct)
    {
        var me = _options.NodeId; var now = ScadaTime.NowMs(); var floor = Math.Max(_state.Term, _peer?.Term ?? 0);
        var lease = await _witness!.UpdateAsync(l =>
            l is null || l.HandoverTo == me || l.Holder == me && l.HandoverTo is null || now > l.ExpiresTs + _options.ClockSkewMs || l.ExpiresTs == 0
                ? new HaLease(me, Math.Max(l?.Term ?? 0, floor) + 1, now + _options.LeaseMs) : l, ct);
        _lease = lease;
        if (lease?.Holder != me || lease.HandoverTo is not null) return false;
        _localLeaseDeadline = Environment.TickCount64 + _options.LeaseMs - _options.ClockSkewMs;
        await PromoteAsync(lease.Term, reason, (userId, actor), ct);
        return true;
    }

    private async Task PromoteAsync(long term, string reason, (int? Id, string? Name)? actor, CancellationToken ct)
    {
        _state.Term = term; await SaveStateAsync();
        _gapStreams.Clear();
        Interlocked.Exchange(ref _promotedTs, ScadaTime.NowMs());
        _primary = true;
        Interlocked.Increment(ref _generation); Interlocked.Increment(ref _switchovers);
        Record("promote", $"本机成为主机：{reason}", actor?.Name);
        await AuditAsync(actor?.Id, actor?.Name ?? "system", "主备切换：本机成为主机", reason, ct);
        _logger.LogWarning("主备：节点 {Node} 成为主机（任期 {Term}）：{Reason}", _options.NodeId, term, reason);
    }

    private async Task DemoteAsync(string reason, (int? Id, string? Name)? actor, CancellationToken ct)
    {
        if (!_primary) return;
        await AuditAsync(actor?.Id, actor?.Name ?? "system", "主备切换：本机降为备机", reason, ct);
        _primary = false;
        Interlocked.Increment(ref _generation);
        Record("demote", $"本机降为备机：{reason}", actor?.Name);
        _logger.LogWarning("主备：节点 {Node} 降为备机：{Reason}", _options.NodeId, reason);
    }

    private async Task ReplicateAsync(bool force, CancellationToken ct)
    {
        var tick = Environment.TickCount64;
        var config = force || tick >= _configDue; var history = force || tick >= _historyDue;
        if (!config && !history) return;
        if (config) _configDue = tick + _options.ConfigSyncMs;
        if (history) _historyDue = tick + _options.HistorySyncMs;
        await _replication.SyncAsync(_peerClient!, _state, SaveStateAsync, config, history, force, ct);
    }

    // 手动切换（在主机上发起）：先停止采集与控制，交接租约，等备机完成最终同步并接管；失败则恢复本机为主机。
    public async Task SwitchoverAsync(int? userId, string actor, string reason, CancellationToken ct)
    {
        if (!_options.Enabled) throw new HaException("未启用主备");
        await _gate.WaitAsync(ct);
        try
        {
            if (!_primary) throw new HaException("本节点不是主机，不能发起切换");
            var peer = await _peerClient!.StatusAsync(ct);
            if (peer is null) throw new HaException("备机不在线，不能切换");
            if (peer.Role != "Standby") throw new HaException($"对端角色为 {peer.Role}，不能切换");
            var term = _state.Term;
            await AuditAsync(userId, actor, "主备手动切换：发起", $"交给 {peer.NodeId}；原因：{reason}", ct);
            await DemoteAsync($"手动切换给 {peer.NodeId}（{actor}）", (userId, actor), ct);
            if (_witness is not null)
                _lease = await _witness.UpdateAsync(l => l is not null && l.Holder == _options.NodeId && l.Term == term ? l with { ExpiresTs = ScadaTime.NowMs(), HandoverTo = peer.NodeId } : l, ct);
            try
            {
                await _peerClient.PostAsync("handover", new HaHandoverRequest(_options.NodeId, term, actor, reason), 60000, ct);
                Record("handover", $"已交接给 {peer.NodeId}", actor);
            }
            catch (Exception ex) when (ex is HaException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                var recovered = _witness is null || (await _witness.UpdateAsync(l => l is not null && l.Holder == _options.NodeId && l.Term == term && l.HandoverTo is not null
                    ? new HaLease(_options.NodeId, term, ScadaTime.NowMs() + _options.LeaseMs) : l, ct))?.Holder == _options.NodeId;
                if (recovered)
                {
                    _localLeaseDeadline = Environment.TickCount64 + _options.LeaseMs - _options.ClockSkewMs;
                    await PromoteAsync(term, $"切换失败（{ex.Message}），恢复本机为主机", (userId, actor), ct);
                }
                throw new HaException($"切换失败：{ex.Message}" + (recovered ? "；已恢复本机为主机" : ""), 502);
            }
        }
        finally { _gate.Release(); }
    }

    // 对端发起的交接：最终同步后接管。
    public async Task AcceptHandoverAsync(HaHandoverRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_primary) return;
            await _replication.SyncAsync(_peerClient!, _state, SaveStateAsync, true, true, true, ct);
            var reason = $"手动切换：{request.Actor} 在 {request.From} 发起；原因：{request.Reason}";
            if (_witness is not null)
            {
                if (!await AcquireAndPromoteAsync(reason, null, request.Actor, ct)) throw new HaException("仲裁租约未交接给本节点");
            }
            else await PromoteAsync(Math.Max(_state.Term, request.Term) + 1, reason, (null, request.Actor), ct);
        }
        finally { _gate.Release(); }
    }

    // 在备机上人工接管：主机在线则转交主机执行正常切换；主机不在线时须仲裁确认租约已过期，无仲裁时必须显式强制。
    public async Task PromoteManuallyAsync(int? userId, string actor, string reason, bool force, CancellationToken ct)
    {
        if (!_options.Enabled) throw new HaException("未启用主备");
        var peer = await _peerClient!.StatusAsync(ct);
        if (_primary) throw new HaException("本节点已是主机");
        if (peer?.Role == "Primary")
        {
            await _peerClient.PostAsync("switchover", new HaForwardRequest(actor, reason), 90000, ct);
            return;
        }
        await _gate.WaitAsync(ct);
        try
        {
            if (_primary) return;
            if (_witness is not null)
            {
                var lease = await _witness.ReadAsync(ct);
                if (lease is not null && lease.Holder != _options.NodeId && lease.ExpiresTs + _options.ClockSkewMs >= ScadaTime.NowMs() && lease.ExpiresTs != 0)
                    throw new HaException($"主机 {lease.Holder} 的租约仍有效，不能接管；请检查主机状态或等待租约过期");
                if (!await AcquireAndPromoteAsync($"人工接管：{reason}", userId, actor, ct)) throw new HaException("未能取得仲裁租约");
            }
            else
            {
                if (peer is null && !force) throw new HaException("无仲裁且无法联系主机：请确认原主机已停机后选择“强制接管”");
                await PromoteAsync(Math.Max(_state.Term, peer?.Term ?? 0) + 1, (peer is null ? "强制接管（无仲裁）：" : "人工接管：") + reason, (userId, actor), ct);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task AuditAsync(int? userId, string actor, string action, string result, CancellationToken ct)
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = userId, UserName = actor, Ts = ScadaTime.NowMs(), Action = action, Result = result.Length > 500 ? result[..500] : result,
                TargetType = "HaNode", TargetId = _options.NodeId, Details = JsonSerializer.Serialize(new { node = _options.NodeId, term = _state.Term, peer = _peer?.NodeId })
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogWarning(ex, "写主备审计失败"); }
    }

    private void Record(string kind, string message, string? actor = null)
    {
        var e = new HaEvent(ScadaTime.NowMs(), kind, message, _state.Term, actor);
        lock (_events) { _events.AddLast(e); while (_events.Count > 200) _events.RemoveFirst(); }
        try { File.AppendAllText(Path.Combine(_stateDir, "ha-events.ndjson"), JsonSerializer.Serialize(e, HaReplication.Json) + "\n"); }
        catch (IOException ex) { _logger.LogWarning(ex, "写主备事件日志失败"); }
    }

    private IEnumerable<HaEvent> ReadEventLines()
    {
        var path = Path.Combine(_stateDir, "ha-events.ndjson");
        if (!File.Exists(path)) return [];
        var list = new List<HaEvent>();
        foreach (var line in File.ReadLines(path).TakeLast(200))
            try { if (JsonSerializer.Deserialize<HaEvent>(line, HaReplication.Json) is { } e) list.Add(e); } catch (JsonException) { }
        return list;
    }

    private HaNodeState LoadState()
    {
        var path = Path.Combine(_stateDir, "ha-node.json");
        try { return File.Exists(path) ? JsonSerializer.Deserialize<HaNodeState>(File.ReadAllText(path), HaReplication.Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private async Task SaveStateAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            var path = Path.Combine(_stateDir, "ha-node.json");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(_state, HaReplication.Json));
            File.Move(path + ".tmp", path, true);
        }
        finally { _saveGate.Release(); }
    }

    public override void Dispose() { _peerClient?.Dispose(); base.Dispose(); }
}
