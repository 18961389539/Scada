using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Aggregation;

public sealed class AggregationService : BackgroundService
{
    private const int BatchSize = 128;
    private readonly IHistoryStore _store;
    private readonly IHistoryBackend _backend;
    private readonly ScadaSettings _settings;
    private readonly ILogger<AggregationService> _logger;
    private readonly INodeRole _role;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public AggregationService(
        IHistoryStore store,
        IOptions<ScadaSettings> settings,
        ILogger<AggregationService> logger,
        IHistoryBackend backend,
        INodeRole? role = null)
    {
        _store = store;
        _backend = backend;
        _settings = settings.Value;
        _logger = logger;
        _role = role ?? new StandaloneNodeRole();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.AggregationEnabled)
        {
            _logger.LogInformation("聚合任务未启用");
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var processed = 0;
                try
                {
                    processed = await RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "读取聚合待办失败，将在下一轮重试");
                }
                await Task.Delay(processed > 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            if (!_settings.AggregationEnabled || !_role.IsActive) return 0;
            var generation = _role.Generation;
            var processed = 0;
            foreach (var raw in new[] { true, false })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentRole(generation)) return processed;
                var windowMs = raw ? PendingAggregation.FiveMinuteMs : PendingAggregation.HourMs;
                var latestStart = now.ToUnixTimeMilliseconds() - windowMs;
                IReadOnlyList<HistoryPendingWindow> pending;
                try
                {
                    pending = await _backend.ListPendingWindowsAsync(raw, latestStart, BatchSize, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "读取聚合待办失败，将在下一轮重试");
                    continue;
                }
                foreach (var window in pending)
                {
                    try
                    {
                        if (!IsCurrentRole(generation)) return processed;
                        if (!raw && await _backend.HasPendingRawWindowsAsync(window.Ts, window.Ts + window.WindowMs, cancellationToken)) continue;
                        if (!await AggregateAsync(window, generation, cancellationToken)) return processed;
                        if (!IsCurrentRole(generation)) return processed;
                        // A source write during calculation changes the revision, preserving the retry.
                        await _backend.AcknowledgePendingWindowAsync(window, cancellationToken);
                        processed++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "聚合历史窗口 {Shard} @ {Ts} 失败，未完成窗口保留待重试", window.Shard, window.Ts);
                    }
                }
            }
            return processed;
        }
        finally { _runLock.Release(); }
    }

    /// <summary>
    /// 按当前口径（排除仿真点的均/最小/最大）可选重建已有聚合分片。不改写原始历史，也不在启动时自动执行。
    /// </summary>
    public async Task<(int FiveMinuteWindows, int HourWindows)> RebuildAsync(long from, long to, CancellationToken cancellationToken)
    {
        await _runLock.WaitAsync(cancellationToken);
        try
        {
            if (!_role.IsActive) return (0, 0);
            if (to <= from) throw new ArgumentOutOfRangeException(nameof(to), "时间范围无效");
            if (to - from > 366L * 24 * 3600_000) throw new ArgumentOutOfRangeException(nameof(to), "重建跨度不能超过 366 天");
            var generation = _role.Generation;
            var five = 0;
            var start = from - ((from % PendingAggregation.FiveMinuteMs) + PendingAggregation.FiveMinuteMs) % PendingAggregation.FiveMinuteMs;
            for (var ts = start; ts < to; ts += PendingAggregation.FiveMinuteMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentRole(generation)) break;
                var values = await _store.QueryRawAggregatesAsync(ts, ts + PendingAggregation.FiveMinuteMs, cancellationToken);
                await _store.WriteAggregate5MinAsync(values, cancellationToken);
                five++;
            }
            var hour = 0;
            var hourStart = from - ((from % PendingAggregation.HourMs) + PendingAggregation.HourMs) % PendingAggregation.HourMs;
            for (var ts = hourStart; ts < to; ts += PendingAggregation.HourMs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentRole(generation)) break;
                var values = await _store.Query5MinAggregatesAsync(ts, ts + PendingAggregation.HourMs, cancellationToken);
                await _store.WriteAggregateHourAsync(values.Select(x => x with { Ts = ts }).ToList(), cancellationToken);
                hour++;
            }
            return (five, hour);
        }
        finally { _runLock.Release(); }
    }

    private bool IsCurrentRole(long generation) => _role.IsActive && _role.Generation == generation;

    private async Task<bool> AggregateAsync(HistoryPendingWindow window, long generation, CancellationToken cancellationToken)
    {
        var end = window.Ts + window.WindowMs;
        if (window.Raw)
        {
            var values = await _store.QueryRawAggregatesAsync(window.Ts, end, cancellationToken);
            if (!IsCurrentRole(generation)) return false;
            await _store.WriteAggregate5MinAsync(values, cancellationToken);
        }
        else
        {
            var values = await _store.Query5MinAggregatesAsync(window.Ts, end, cancellationToken);
            if (!IsCurrentRole(generation)) return false;
            await _store.WriteAggregateHourAsync(values.Select(x => x with { Ts = window.Ts }).ToList(), cancellationToken);
        }
        return true;
    }
}
