using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Health;
using Scada.Engine.Maintenance;

namespace Scada.Engine.Control;

public sealed record MomentaryResult(ControlSubmissionResult Result, Guid? LeaseId = null);

public sealed record MomentaryReleaseBeforeDeleteResult(bool Success, string? Error, IReadOnlyList<int> FailedTagIds)
{
    public static MomentaryReleaseBeforeDeleteResult Ok { get; } = new(true, null, []);
}

// 点动与瞬时按钮的保持租约：按下成功后由看门狗按期释放，瞬时按钮在脉冲宽度后自动释放。
// 释放跳过联锁以保证失电优先；同一测点同时只允许一个点动租约。
// 租约写入运行库，进程崩溃或掉电后启动先补写释放值，避免现场输出停在“按下”状态。
public sealed record MomentaryLeaseStatus(
    Guid Id, int TagId, int? DeviceId, double ReleaseValue, string Source, string UserName,
    MomentaryLeaseKind Kind, MomentaryLeaseState State, long CreatedTs, long ExpiresTs, bool ReleaseRetrying);

public sealed class MomentaryControlService : BackgroundService, IHealthContributor
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private const long RecoveryRetryMs = 10_000;
    private const long ReleaseRetryMs = 1_000;
    private const long ReconcileIntervalMs = 1_000;
    public const string WarningCode = "momentary_lease_unreleased";
    private readonly ControlCommandService _commands;
    private readonly IDbContextFactory<RuntimeDbContext> _runtimeFactory;
    private readonly ILogger<MomentaryControlService> _logger;
    private readonly ConcurrentDictionary<int, Lease> _leases = new();
    private readonly List<Lease> _recoveryPending = [];
    private bool _recoveryLoaded;
    private long _nextRecoveryAttemptTs;
    private long _nextReconcileTs;
    private volatile IReadOnlyList<MomentaryLeaseStatus> _openSnapshot = [];

    public MomentaryControlService(ControlCommandService commands, IDbContextFactory<RuntimeDbContext> runtimeFactory, ILogger<MomentaryControlService> logger)
    {
        _commands = commands;
        _runtimeFactory = runtimeFactory;
        _logger = logger;
    }

    public async Task<MomentaryResult> PressAsync(int tagId, double value, double releaseValue, int maxHoldMs, ControlActor actor,
        string source, Guid? requestId, bool confirmed, CancellationToken ct)
    {
        var lease = NewLease(tagId, releaseValue, actor, source, MomentaryLeaseKind.Press, requestId);
        if (!_leases.TryAdd(tagId, lease))
            return new(new(false, "该测点已由另一次点动保持，请先释放", Outcome: GatewayCommandOutcome.NotSent));
        if (!await PersistNewAsync(lease, ct))
        {
            _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease));
            return new(new(false, "该测点存在未释放的旧点动租约，请先确认现场并完成释放", Outcome: GatewayCommandOutcome.NotSent));
        }

        ControlSubmissionResult result;
        try { result = await _commands.SubmitMomentaryPressAsync(tagId, value, actor, source, lease.Id, confirmed, "点动按下", ct); }
        catch
        {
            await ReleaseLeaseCoreAsync(lease, "按下结果未知", actor);
            throw;
        }
        if (!result.Success)
        {
            if (result.Outcome == GatewayCommandOutcome.NotSent)
            {
                _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease));
                await DeleteLeaseRowAsync(lease.Id);
                return new(result);
            }
            var released = await ReleaseLeaseCoreAsync(lease, "按下失败后释放", actor);
            var message = released.Success
                ? $"{result.Message}；已补写释放值"
                : $"{result.Message}；释放未完成，将自动重试：{released.Message}";
            return new(result with { Message = message }, lease.Id);
        }

        bool releaseNow;
        lock (lease)
        {
            releaseNow = lease.ReleaseRequested;
            if (!releaseNow) { lease.Starting = false; lease.ExpiresTs = ScadaTime.NowMs() + maxHoldMs; }
        }
        if (!releaseNow)
        {
            await MarkActiveAsync(lease);
            return new(result, lease.Id);
        }
        var releasedNow = await ReleaseLeaseCoreAsync(lease, "按下完成前已请求释放", actor);
        return new(result with { Success = releasedNow.Success, Message = $"已按下；收到提前释放请求并已释放{(releasedNow.Success ? "" : $"失败：{releasedNow.Message}")}" }, lease.Id);
    }

    public async Task<MomentaryResult> PulseAsync(int tagId, double value, double releaseValue, int pulseMs, ControlActor actor,
        string source, Guid? requestId, bool confirmed, CancellationToken ct)
    {
        var lease = NewLease(tagId, releaseValue, actor, source, MomentaryLeaseKind.Pulse, requestId);
        if (!_leases.TryAdd(tagId, lease))
            return new(new(false, "该测点已由另一次点动保持，请先释放", Outcome: GatewayCommandOutcome.NotSent));
        if (!await PersistNewAsync(lease, ct))
        {
            _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease));
            return new(new(false, "该测点存在未释放的旧点动租约，请先确认现场并完成释放", Outcome: GatewayCommandOutcome.NotSent));
        }

        ControlSubmissionResult result;
        try { result = await _commands.SubmitMomentaryPressAsync(tagId, value, actor, source, lease.Id, confirmed, "瞬时按下", ct); }
        catch
        {
            await ReleaseLeaseCoreAsync(lease, "瞬时按下结果未知", actor);
            throw;
        }
        if (!result.Success)
        {
            if (result.Outcome == GatewayCommandOutcome.NotSent)
            {
                _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease));
                await DeleteLeaseRowAsync(lease.Id);
                return new(result);
            }
            var released = await ReleaseLeaseCoreAsync(lease, "瞬时按下失败后释放", actor);
            var message = released.Success
                ? $"{result.Message}；已补写释放值"
                : $"{result.Message}；释放未完成，将自动重试：{released.Message}";
            return new(result with { Message = message }, lease.Id);
        }

        lock (lease) { lease.Starting = false; lease.ExpiresTs = ScadaTime.NowMs() + pulseMs; }
        await MarkActiveAsync(lease);
        return new(result with { Message = $"{result.Message}，{pulseMs} 毫秒后自动释放" }, lease.Id);
    }

    // 删除或改写点动目标前的失电优先释放：跳过联锁，释放成功后再允许变更。释放失败则拒绝本次变更。
    public Task<MomentaryReleaseBeforeDeleteResult> ReleaseBeforeDeleteAsync(IEnumerable<int> tagIds, CancellationToken ct) =>
        ReleaseBeforeChangeAsync(tagIds, "删除", ct);

    public async Task<MomentaryReleaseBeforeDeleteResult> ReleaseBeforeChangeAsync(IEnumerable<int> tagIds, string action, CancellationToken ct)
    {
        var ids = tagIds.Distinct().ToArray();
        if (ids.Length == 0) return MomentaryReleaseBeforeDeleteResult.Ok;

        List<MomentaryLease> rows;
        await using (var db = await _runtimeFactory.CreateDbContextAsync(ct))
            rows = await db.MomentaryLeases.AsNoTracking().Where(x => ids.Contains(x.TagId)).ToListAsync(ct);

        var failed = new List<(int TagId, string Message)>();
        var handled = new HashSet<int>();
        foreach (var id in ids)
        {
            if (!_leases.TryGetValue(id, out var lease)) continue;
            handled.Add(id);
            lock (lease) { lease.Releasing = true; }
            var result = await ReleaseLeaseCoreAsync(lease, "配置变更前失电优先释放", lease.Actor);
            if (!result.Success) failed.Add((id, result.Message));
        }

        foreach (var row in rows)
        {
            if (handled.Contains(row.TagId) || _leases.ContainsKey(row.TagId)) continue;
            var lease = LeaseFrom(row);
            var result = await ExecuteReleaseAsync(lease, "配置变更前失电优先释放", ct);
            if (result.Success)
            {
                _recoveryPending.RemoveAll(x => x.Id == lease.Id);
                await DeleteLeaseRowAsync(lease.Id);
            }
            else failed.Add((row.TagId, result.Message));
        }

        if (failed.Count == 0) return MomentaryReleaseBeforeDeleteResult.Ok;
        var tags = string.Join("、", failed.Select(item => $"#{item.TagId}").Distinct());
        return new(false, $"点动输出尚未释放（测点 {tags}），请核对现场后再{action}", failed.Select(item => item.TagId).Distinct().ToArray());
    }

    // 操作人释放：按租约编号优先定位，找不到时按测点兜底；对未按下完成的租约只登记释放请求。
    public async Task<ControlSubmissionResult> ReleaseAsync(Guid? leaseId, int tagId, ControlActor actor, CancellationToken ct)
    {
        var lease = leaseId is Guid id ? _leases.Values.FirstOrDefault(x => x.Id == id) : null;
        if (lease is null && !_leases.TryGetValue(tagId, out lease)) return new(true, "当前没有保持中的点动");
        lock (lease)
        {
            if (lease.Starting) { lease.ReleaseRequested = true; return new(true, "已请求释放，按下完成后立即释放"); }
            if (lease.Releasing) return new(true, "正在释放");
            lease.Releasing = true;
        }
        return await ReleaseLeaseCoreAsync(lease, "操作人释放", actor);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            try { await SweepAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "点动看门狗刷新失败"); }
        }
    }

    // 服务停止属于最后一道释放机会：忽略停止令牌并等待全部释放写入完成。
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        var pending = new List<Task>();
        foreach (var lease in _leases.Values.ToArray())
        {
            lock (lease)
            {
                if (lease.Releasing) continue;
                lease.Releasing = true;
            }
            pending.Add(ReleaseLeaseCoreAsync(lease, "服务停止", lease.Actor));
        }
        if (pending.Count == 0) return;
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
        catch (TimeoutException) { _logger.LogError("点动释放未在服务停止前全部完成"); }
    }

    private Lease NewLease(int tagId, double releaseValue, ControlActor actor, string source, MomentaryLeaseKind kind, Guid? requestId) => new()
    {
        Id = requestId ?? Guid.NewGuid(), TagId = tagId, ReleaseValue = releaseValue, Actor = actor, Source = source, Kind = kind,
        StartDeadlineTs = ScadaTime.NowMs() + (long)StartTimeout.TotalMilliseconds,
        Target = _commands.ResolveReleaseTarget(tagId)
    };

    private static Lease LeaseFrom(MomentaryLease row) => new()
    {
        Id = row.Id, TagId = row.TagId, ReleaseValue = row.ReleaseValue, Actor = new(row.UserId, row.UserName), Source = row.Source,
        Kind = row.Kind, StartDeadlineTs = row.StartDeadlineTs, ExpiresTs = row.ExpiresTs, Starting = row.State == MomentaryLeaseState.Starting,
        ReleaseId = row.ReleaseId, Target = MomentaryReleaseTarget.FromLease(row)
    };

    private async Task<bool> PersistNewAsync(Lease lease, CancellationToken ct)
    {
        try
        {
            await using var db = await _runtimeFactory.CreateDbContextAsync(ct);
            var leftovers = await db.MomentaryLeases.Where(x => x.TagId == lease.TagId).ToListAsync(ct);
            if (leftovers.Count > 0)
            {
                _logger.LogWarning("测点 #{TagId} 存在未释放的点动租约，拒绝新按下", lease.TagId);
                return false;
            }
            var row = new MomentaryLease
            {
                Id = lease.Id, TagId = lease.TagId, ReleaseValue = lease.ReleaseValue, UserId = lease.Actor.UserId, UserName = lease.Actor.UserName,
                Source = lease.Source, Kind = lease.Kind, State = MomentaryLeaseState.Starting, ReleaseId = lease.ReleaseId,
                CreatedTs = ScadaTime.NowMs(), StartDeadlineTs = lease.StartDeadlineTs, ExpiresTs = lease.ExpiresTs
            };
            lease.Target?.CopyTo(row);
            db.MomentaryLeases.Add(row);
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "持久化点动保持租约失败：测点 #{TagId}", lease.TagId);
            return false;
        }
    }

    private async Task MarkActiveAsync(Lease lease)
    {
        try
        {
            await using var db = await _runtimeFactory.CreateDbContextAsync(CancellationToken.None);
            await db.MomentaryLeases.Where(x => x.Id == lease.Id).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.State, MomentaryLeaseState.Active)
                .SetProperty(x => x.ExpiresTs, lease.ExpiresTs), CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogError(ex, "更新点动保持租约失败：测点 #{TagId}", lease.TagId); }
    }

    private async Task DeleteLeaseRowAsync(Guid id)
    {
        try
        {
            await using var db = await _runtimeFactory.CreateDbContextAsync(CancellationToken.None);
            await db.MomentaryLeases.Where(x => x.Id == id).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "删除点动保持租约失败：{LeaseId}", id); }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        List<MomentaryLease> rows;
        await using (var db = await _runtimeFactory.CreateDbContextAsync(ct))
            rows = await db.MomentaryLeases.AsNoTracking().ToListAsync(ct);
        foreach (var row in rows)
        {
            var lease = LeaseFrom(row);
            if (!_leases.TryAdd(lease.TagId, lease)) continue;
            _logger.LogWarning("发现上次进程遗留的点动保持租约，补写释放值：测点 #{TagId}", lease.TagId);
            var result = await ExecuteReleaseAsync(lease, "服务重启恢复", ct);
            _leases.TryRemove(new KeyValuePair<int, Lease>(lease.TagId, lease));
            if (result.Success)
            {
                await DeleteLeaseRowAsync(lease.Id);
                continue;
            }
            lease.NextRetryTs = ScadaTime.NowMs() + RecoveryRetryMs;
            if (!_recoveryPending.Any(x => x.Id == lease.Id)) _recoveryPending.Add(lease);
            _logger.LogError("服务重启补写点动释放失败：测点 #{TagId} 写入 {Value}，{Message}", lease.TagId, lease.ReleaseValue, result.Message);
        }
    }

    private async Task SweepAsync()
    {
        await RecoverIfNeededAsync();
        await RetryRecoveryAsync();
        var now = ScadaTime.NowMs();
        foreach (var lease in _leases.Values.ToArray())
        {
            string reason;
            lock (lease)
            {
                if (lease.Releasing || now < lease.NextRetryTs) continue;
                if (lease.NextRetryTs > 0) reason = "释放重试";
                else if (lease.Starting && now >= lease.StartDeadlineTs) { lease.ReleaseRequested = true; reason = "按下超时"; }
                else if (!lease.Starting && now >= lease.ExpiresTs) reason = lease.Kind == MomentaryLeaseKind.Pulse ? "脉冲结束" : "超过最长保持时间";
                else continue;
                lease.Releasing = true;
            }
            await ReleaseLeaseCoreAsync(lease, reason, lease.Actor);
        }
        PublishOpenSnapshot();
    }

    private async Task RecoverIfNeededAsync()
    {
        if (!_commands.IsActive)
        {
            _recoveryLoaded = false;
            PublishOpenSnapshot();
            return;
        }
        var now = ScadaTime.NowMs();
        if (!_recoveryLoaded)
        {
            if (now < _nextRecoveryAttemptTs) return;
            try
            {
                await RecoverAsync(CancellationToken.None);
                _recoveryLoaded = true;
                _nextRecoveryAttemptTs = 0;
                _nextReconcileTs = now + ReconcileIntervalMs;
                PublishOpenSnapshot();
            }
            catch (Exception ex)
            {
                _nextRecoveryAttemptTs = now + RecoveryRetryMs;
                _logger.LogError(ex, "恢复点动保持租约失败");
            }
            return;
        }
        if (now < _nextReconcileTs) return;
        try
        {
            await AdoptOrphanLeasesAsync(CancellationToken.None);
            _nextReconcileTs = now + ReconcileIntervalMs;
            PublishOpenSnapshot();
        }
        catch (Exception ex)
        {
            _nextReconcileTs = now + ReconcileIntervalMs;
            _logger.LogError(ex, "对账点动保持租约失败");
        }
    }

    // 升主后不能只扫一次库：运行库同步迟到的租约行必须被看门狗接手并按恢复路径释放。
    internal async Task AdoptOrphanLeasesAsync(CancellationToken ct)
    {
        List<MomentaryLease> rows;
        await using (var db = await _runtimeFactory.CreateDbContextAsync(ct))
            rows = await db.MomentaryLeases.AsNoTracking().ToListAsync(ct);
        foreach (var row in rows)
        {
            if (_leases.ContainsKey(row.TagId) || _recoveryPending.Any(x => x.Id == row.Id)) continue;
            var lease = LeaseFrom(row);
            if (!_leases.TryAdd(lease.TagId, lease)) continue;
            _logger.LogWarning("发现运行库中尚未接手的点动保持租约，补写释放值：测点 #{TagId}", lease.TagId);
            var result = await ExecuteReleaseAsync(lease, "周期对账恢复", ct);
            _leases.TryRemove(new KeyValuePair<int, Lease>(lease.TagId, lease));
            if (result.Success)
            {
                await DeleteLeaseRowAsync(lease.Id);
                continue;
            }
            lease.NextRetryTs = ScadaTime.NowMs() + RecoveryRetryMs;
            if (!_recoveryPending.Any(x => x.Id == lease.Id)) _recoveryPending.Add(lease);
            _logger.LogError("周期对账补写点动释放失败：测点 #{TagId} 写入 {Value}，{Message}", lease.TagId, lease.ReleaseValue, result.Message);
        }
    }

    public IReadOnlyList<MomentaryLeaseStatus> ListOpen() => _openSnapshot;

    public async Task<IReadOnlyList<MomentaryLeaseStatus>> ListPersistedAsync(CancellationToken ct = default)
    {
        List<MomentaryLease> rows;
        await using (var db = await _runtimeFactory.CreateDbContextAsync(ct))
            rows = await db.MomentaryLeases.AsNoTracking().OrderBy(x => x.ExpiresTs).ToListAsync(ct);
        var retrying = _recoveryPending.Select(x => x.Id).ToHashSet();
        var held = _leases.Values.ToDictionary(x => x.Id);
        return rows.Select(row =>
        {
            var memory = held.GetValueOrDefault(row.Id);
            var isRetrying = retrying.Contains(row.Id) || memory is { NextRetryTs: > 0 };
            return new MomentaryLeaseStatus(
                row.Id, row.TagId, row.TargetDeviceId ?? memory?.Target?.DeviceId, row.ReleaseValue, row.Source, row.UserName,
                row.Kind, row.State, row.CreatedTs, row.ExpiresTs, isRetrying);
        }).ToList();
    }


    public IEnumerable<SystemWarning> HealthWarnings()
    {
        foreach (var item in _openSnapshot)
        {
            var retry = item.ReleaseRetrying ? "，释放失败正在重试" : "";
            yield return new SystemWarning(WarningCode,
                $"点动输出未释放：测点 #{item.TagId}，释放值 {item.ReleaseValue}，来源 {item.Source}{retry}；请核对现场。此为运维告警，不计入官方工艺报警 KPI");
        }
    }

    private void PublishOpenSnapshot()
    {
        var map = new Dictionary<Guid, MomentaryLeaseStatus>();
        foreach (var lease in _recoveryPending)
            map[lease.Id] = ToStatus(lease, retrying: true, state: MomentaryLeaseState.Active);
        foreach (var lease in _leases.Values)
            map.TryAdd(lease.Id, ToStatus(lease, retrying: lease.NextRetryTs > 0, state: lease.Starting ? MomentaryLeaseState.Starting : MomentaryLeaseState.Active));
        _openSnapshot = map.Values.OrderBy(x => x.TagId).ToList();
    }

    private static MomentaryLeaseStatus ToStatus(Lease lease, bool retrying, MomentaryLeaseState state) => new(
        lease.Id, lease.TagId, lease.Target?.DeviceId, lease.ReleaseValue, lease.Source, lease.Actor.UserName,
        lease.Kind, state, lease.StartDeadlineTs, lease.ExpiresTs, retrying);

    private async Task RetryRecoveryAsync()
    {
        if (_recoveryPending.Count == 0 || !_commands.IsActive) return;
        var now = ScadaTime.NowMs();
        foreach (var lease in _recoveryPending.ToArray())
        {
            if (now < lease.NextRetryTs) continue;
            var result = await ExecuteReleaseAsync(lease, "恢复释放重试", CancellationToken.None);
            if (!result.Success)
            {
                lease.NextRetryTs = now + RecoveryRetryMs;
                continue;
            }
            _recoveryPending.Remove(lease);
            await DeleteLeaseRowAsync(lease.Id);
        }
        PublishOpenSnapshot();
    }

    private async Task<ControlSubmissionResult> ReleaseLeaseCoreAsync(Lease lease, string reason, ControlActor actor)
    {
        var result = await ExecuteReleaseAsync(lease, reason, CancellationToken.None);
        if (!result.Success)
        {
            lock (lease)
            {
                lease.Releasing = false;
                lease.NextRetryTs = ScadaTime.NowMs() + ReleaseRetryMs;
            }
            _logger.LogError("点动释放失败：测点 #{TagId} 写入 {Value}，原因 {Reason}，结果 {Message}；将自动重试", lease.TagId, lease.ReleaseValue, reason, result.Message);
            return result with { Message = result.Message + "；将自动重试释放" };
        }
        _leases.TryRemove(new KeyValuePair<int, Lease>(lease.TagId, lease));
        _recoveryPending.RemoveAll(x => x.Id == lease.Id);
        await DeleteLeaseRowAsync(lease.Id);
        return result;
    }

    private async Task<ControlSubmissionResult> ExecuteReleaseAsync(Lease lease, string reason, CancellationToken ct)
    {
        ControlSubmissionResult result = new(false, "未执行释放", Outcome: GatewayCommandOutcome.NotSent);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                result = await _commands.SubmitMomentaryReleaseAsync(lease.TagId, lease.ReleaseValue, lease.Actor,
                    $"{lease.Source}（{reason}）", lease.ReleaseId, timeout.Token, lease.Target);
            }
            catch (Exception ex) { result = new(false, ex.Message, Outcome: GatewayCommandOutcome.Unknown); }
            if (result.Success || result.Outcome is GatewayCommandOutcome.Unknown or GatewayCommandOutcome.Failed) break;
            if (result.Outcome != GatewayCommandOutcome.NotSent) break;
            await Task.Delay(200);
        }
        return result;
    }

    private sealed class Lease
    {
        public required Guid Id { get; init; }
        public required int TagId { get; init; }
        public required double ReleaseValue { get; init; }
        public required ControlActor Actor { get; init; }
        public required string Source { get; init; }
        public required MomentaryLeaseKind Kind { get; init; }
        public required long StartDeadlineTs { get; init; }
        public MomentaryReleaseTarget? Target { get; init; }
        public bool Starting { get; set; } = true;
        public bool ReleaseRequested { get; set; }
        public bool Releasing { get; set; }
        public long ExpiresTs { get; set; }
        public long NextRetryTs { get; set; }
        public Guid ReleaseId { get; set; } = Guid.NewGuid();
    }
}