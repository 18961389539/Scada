using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;

namespace Scada.Engine.Aggregation;

public sealed class AggregationService : BackgroundService
{
    private readonly IHistoryStore _store;
    private readonly ScadaSettings _settings;
    private readonly ILogger<AggregationService> _logger;
    private readonly Scada.Core.Interfaces.INodeRole? _role;

    public AggregationService(
        IHistoryStore store,
        IOptions<ScadaSettings> settings,
        ILogger<AggregationService> logger,
        Scada.Core.Interfaces.INodeRole? role = null)
    {
        _role = role;
        _store = store;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.AggregationEnabled)
        {
            _logger.LogInformation("聚合任务未启用");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        await BackfillAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var boundary = NextFiveMinuteBoundary(now);
            var delay = boundary - now;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, stoppingToken);
            }

            var windowEnd = boundary;
            if (_role is { IsActive: false }) continue; // 备机的聚合数据由主机复制，不自行计算，避免用不完整的原始数据覆盖
            var windowStart = windowEnd.AddMinutes(-5);
            await AggregateFiveMinutesAsync(windowStart, windowEnd, stoppingToken);

            if (windowEnd.Minute == 0)
            {
                await AggregateHourAsync(windowEnd.AddHours(-1), windowEnd, stoppingToken);
            }
        }
    }

    private async Task BackfillAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var windowEnd = NextFiveMinuteBoundary(now).AddMinutes(-5);
            var windowStart = windowEnd.AddMinutes(-5);
            await AggregateFiveMinutesAsync(windowStart, windowEnd, cancellationToken);

            if (now.Minute < 5)
            {
                var hourEnd = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
                await AggregateHourAsync(hourEnd.AddHours(-1), hourEnd, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "启动时补算聚合数据失败");
        }
    }

    private async Task AggregateFiveMinutesAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        try
        {
            var values = await _store.QueryRawAggregatesAsync(
                start.ToUnixTimeMilliseconds(),
                end.ToUnixTimeMilliseconds(),
                cancellationToken);
            if (values.Count == 0)
            {
                return;
            }

            await _store.WriteAggregate5MinAsync(values, cancellationToken);
            _logger.LogInformation("完成 5 分钟聚合：{Start:HH:mm} - {End:HH:mm}，{Count} 个测点", start, end, values.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "5 分钟聚合失败：{Start:HH:mm} - {End:HH:mm}", start, end);
        }
    }

    private async Task AggregateHourAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        try
        {
            var values = await _store.Query5MinAggregatesAsync(
                start.ToUnixTimeMilliseconds(),
                end.ToUnixTimeMilliseconds(),
                cancellationToken);
            if (values.Count == 0)
            {
                return;
            }

            var normalized = values
                .Select(x => x with { Ts = start.ToUnixTimeMilliseconds() })
                .ToList();
            await _store.WriteAggregateHourAsync(normalized, cancellationToken);
            _logger.LogInformation("完成小时聚合：{Start:yyyy-MM-dd HH}:00，{Count} 个测点", start, normalized.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "小时聚合失败：{Start:yyyy-MM-dd HH}:00", start);
        }
    }

    private static DateTimeOffset NextFiveMinuteBoundary(DateTimeOffset now)
    {
        var aligned = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero);
        var next = aligned.AddMinutes(5 - now.Minute % 5);
        if (next <= now)
        {
            next = next.AddMinutes(5);
        }

        return next;
    }
}
