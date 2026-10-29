using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Data;
using Scada.Data.Infrastructure;

namespace Scada.Engine.Aggregation;

public sealed class AggregationService : BackgroundService
{
    private const int BatchSize = 128;
    private readonly IHistoryStore _store;
    private readonly ScadaSettings _settings;
    private readonly ILogger<AggregationService> _logger;
    private readonly IHistoryDbFactory _factory;
    private readonly INodeRole _role;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public AggregationService(
        IHistoryStore store,
        IOptions<ScadaSettings> settings,
        ILogger<AggregationService> logger,
        IHistoryDbFactory factory,
        INodeRole? role = null)
    {
        _store = store;
        _settings = settings.Value;
        _logger = logger;
        _factory = factory;
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
            if (!_settings.AggregationEnabled || !_role.IsActive || !Directory.Exists(_settings.HistoryRoot)) return 0;
            var generation = _role.Generation;
            var processed = 0;
            foreach (var raw in new[] { true, false })
            {
                foreach (var path in Directory.EnumerateFiles(_settings.HistoryRoot, raw ? "raw_*.db" : "agg_*.db").Order(StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!IsCurrentRole(generation)) return processed;
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!DateTime.TryParseExact(name[4..], raw ? "yyyyMM" : "yyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)) continue;
                    try
                    {
                        await using var db = raw ? _factory.CreateRaw(date.Year, date.Month) : _factory.CreateAggregate(date.Year);
                        var windowMs = raw ? PendingAggregation.FiveMinuteMs : PendingAggregation.HourMs;
                        var latestStart = now.ToUnixTimeMilliseconds() - windowMs;
                        var pending = await db.PendingAggregations.AsNoTracking()
                            .Where(x => x.WindowMs == windowMs && x.Ts <= latestStart)
                            .OrderBy(x => x.Ts).Take(BatchSize).ToListAsync(cancellationToken);
                        foreach (var window in pending)
                        {
                            if (!IsCurrentRole(generation)) return processed;
                            if (!raw && await HasPendingRawAsync(window, cancellationToken)) continue;
                            if (!await AggregateAsync(window, raw, generation, cancellationToken)) return processed;
                            if (!IsCurrentRole(generation)) return processed;
                            // A source write during calculation changes the revision, preserving the retry.
                            await db.PendingAggregations
                                .Where(x => x.WindowMs == window.WindowMs && x.Ts == window.Ts && x.Revision == window.Revision)
                                .ExecuteDeleteAsync(cancellationToken);
                            processed++;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "聚合历史库 {File} 失败，未完成窗口保留待重试", path);
                    }
                }
            }
            return processed;
        }
        finally { _runLock.Release(); }
    }

    private bool IsCurrentRole(long generation) => _role.IsActive && _role.Generation == generation;

    private async Task<bool> HasPendingRawAsync(PendingAggregation window, CancellationToken cancellationToken)
    {
        var start = DateTimeOffset.FromUnixTimeMilliseconds(window.Ts);
        if (!File.Exists(_factory.GetRawPath(start.Year, start.Month))) return false;
        await using var db = _factory.CreateRaw(start.Year, start.Month);
        var end = window.Ts + window.WindowMs;
        return await db.PendingAggregations.AnyAsync(
            x => x.WindowMs == PendingAggregation.FiveMinuteMs && x.Ts >= window.Ts && x.Ts < end, cancellationToken);
    }

    private async Task<bool> AggregateAsync(PendingAggregation window, bool raw, long generation, CancellationToken cancellationToken)
    {
        var end = window.Ts + window.WindowMs;
        if (raw)
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
