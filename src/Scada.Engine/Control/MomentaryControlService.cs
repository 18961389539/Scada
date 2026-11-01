using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Models;

namespace Scada.Engine.Control;

public sealed record MomentaryResult(ControlSubmissionResult Result, Guid? LeaseId = null);

// 点动与瞬时按钮的保持租约：按下成功后由看门狗按期释放，瞬时按钮在脉冲宽度后自动释放。
// 释放跳过联锁以保证失电优先；同一测点同时只允许一个点动租约。
public sealed class MomentaryControlService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);
    private readonly ControlCommandService _commands;
    private readonly ILogger<MomentaryControlService> _logger;
    private readonly ConcurrentDictionary<int, Lease> _leases = new();

    public MomentaryControlService(ControlCommandService commands, ILogger<MomentaryControlService> logger)
    {
        _commands = commands;
        _logger = logger;
    }

    public async Task<MomentaryResult> PressAsync(int tagId, double value, double releaseValue, int maxHoldMs, ControlActor actor,
        string source, Guid? requestId, bool confirmed, CancellationToken ct)
    {
        var lease = NewLease(tagId, releaseValue, actor, source, LeaseKind.Press, requestId);
        if (!_leases.TryAdd(tagId, lease))
            return new(new(false, "该测点已由另一次点动保持，请先释放", Outcome: GatewayCommandOutcome.NotSent));

        ControlSubmissionResult result;
        try { result = await _commands.SubmitMomentaryPressAsync(tagId, value, actor, source, lease.Id, confirmed, "点动按下", ct); }
        catch { _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease)); throw; }
        if (!result.Success)
        {
            _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease));
            return new(result);
        }

        bool releaseNow;
        lock (lease)
        {
            releaseNow = lease.ReleaseRequested;
            if (!releaseNow) { lease.Starting = false; lease.ExpiresTs = ScadaTime.NowMs() + maxHoldMs; }
        }
        if (!releaseNow) return new(result, lease.Id);
        // 按下写入完成前已收到释放请求：再写一次释放值，覆盖两种写入顺序。
        var released = await ReleaseLeaseCoreAsync(lease, "按下完成前已请求释放", actor);
        return new(result with { Success = released.Success, Message = $"已按下；收到提前释放请求并已释放{(released.Success ? "" : $"失败：{released.Message}")}" }, lease.Id);
    }

    public async Task<MomentaryResult> PulseAsync(int tagId, double value, double releaseValue, int pulseMs, ControlActor actor,
        string source, Guid? requestId, bool confirmed, CancellationToken ct)
    {
        var lease = NewLease(tagId, releaseValue, actor, source, LeaseKind.Pulse, requestId);
        if (!_leases.TryAdd(tagId, lease))
            return new(new(false, "该测点已由另一次点动保持，请先释放", Outcome: GatewayCommandOutcome.NotSent));

        ControlSubmissionResult result;
        try { result = await _commands.SubmitMomentaryPressAsync(tagId, value, actor, source, lease.Id, confirmed, "瞬时按下", ct); }
        catch { _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease)); throw; }
        if (!result.Success)
        {
            _leases.TryRemove(new KeyValuePair<int, Lease>(tagId, lease));
            return new(result);
        }

        lock (lease) { lease.Starting = false; lease.ExpiresTs = ScadaTime.NowMs() + pulseMs; }
        return new(result with { Message = $"{result.Message}，{pulseMs} 毫秒后自动释放" }, lease.Id);
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

    private Lease NewLease(int tagId, double releaseValue, ControlActor actor, string source, LeaseKind kind, Guid? requestId) => new()
    {
        Id = requestId ?? Guid.NewGuid(), TagId = tagId, ReleaseValue = releaseValue, Actor = actor, Source = source, Kind = kind,
        StartDeadlineTs = ScadaTime.NowMs() + (long)StartTimeout.TotalMilliseconds
    };

    private async Task SweepAsync()
    {
        var now = ScadaTime.NowMs();
        foreach (var lease in _leases.Values.ToArray())
        {
            string reason;
            lock (lease)
            {
                if (lease.Releasing) continue;
                if (lease.Starting && now >= lease.StartDeadlineTs) { lease.ReleaseRequested = true; reason = "按下超时"; }
                else if (!lease.Starting && now >= lease.ExpiresTs) reason = lease.Kind == LeaseKind.Pulse ? "脉冲结束" : "超过最长保持时间";
                else continue;
                lease.Releasing = true;
            }
            await ReleaseLeaseCoreAsync(lease, reason, lease.Actor);
        }
    }

    private async Task<ControlSubmissionResult> ReleaseLeaseCoreAsync(Lease lease, string reason, ControlActor actor)
    {
        ControlSubmissionResult result = new(false, "未执行释放", Outcome: GatewayCommandOutcome.NotSent);
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                result = await _commands.SubmitMomentaryReleaseAsync(lease.TagId, lease.ReleaseValue, actor,
                    $"{lease.Source}（{reason}）", Guid.NewGuid(), timeout.Token);
            }
            catch (Exception ex) { result = new(false, ex.Message, Outcome: GatewayCommandOutcome.Unknown); }
            if (result.Success) break;
            // 测点/设备配置问题重试也不会成功；通信或回读失败再试。
            if (result.Outcome == GatewayCommandOutcome.NotSent) break;
            await Task.Delay(200);
        }
        _leases.TryRemove(new KeyValuePair<int, Lease>(lease.TagId, lease));
        if (!result.Success)
            _logger.LogError("点动释放失败：测点 #{TagId} 写入 {Value}，原因 {Reason}，结果 {Message}", lease.TagId, lease.ReleaseValue, reason, result.Message);
        return result;
    }

    private enum LeaseKind { Press, Pulse }

    private sealed class Lease
    {
        public required Guid Id { get; init; }
        public required int TagId { get; init; }
        public required double ReleaseValue { get; init; }
        public required ControlActor Actor { get; init; }
        public required string Source { get; init; }
        public required LeaseKind Kind { get; init; }
        public required long StartDeadlineTs { get; init; }
        public bool Starting { get; set; } = true;
        public bool ReleaseRequested { get; set; }
        public bool Releasing { get; set; }
        public long ExpiresTs { get; set; }
    }
}
