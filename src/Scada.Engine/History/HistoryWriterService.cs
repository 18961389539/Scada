using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;

namespace Scada.Engine.History;

public sealed class HistoryWriterService : BackgroundService
{
    private readonly IRealtimeBus _bus;
    private readonly IRealtimeCache _cache;
    private readonly IConfigSnapshot _config;
    private readonly IHistoryStore _store;
    private readonly ScadaSettings _settings;
    private readonly HistorySpool _spool;
    private readonly ILogger<HistoryWriterService> _logger;
    private long _reportedBusDrops;
    private bool _spoolFailed;
    private long _spoolWarningAt;

    private readonly Dictionary<int, ArchiveState> _lastStored = new();
    private readonly Dictionary<int, ArchiveState> _lastBuffered = new();
    private readonly List<HistoryValue> _buffer = new();
    private Dictionary<int, Tag> _tags = new();
    private int _configVersion = -1;

    public HistoryWriterService(
        IRealtimeBus bus,
        IRealtimeCache cache,
        IConfigSnapshot config,
        IHistoryStore store,
        IOptions<ScadaSettings> settings,
        HistorySpool spool,
        ILogger<HistoryWriterService> logger)
    {
        _bus = bus;
        _cache = cache;
        _config = config;
        _store = store;
        _settings = settings.Value;
        _spool = spool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _bus.Subscribe();
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(_settings.ArchiveFlushIntervalMs, 200)));
        var tickTask = ticker.WaitForNextTickAsync(stoppingToken).AsTask();

        await RecoverSpoolAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ReloadConfigIfNeeded();
                var dataTask = reader.WaitToReadAsync(stoppingToken).AsTask();
                var completed = await Task.WhenAny(tickTask, dataTask);

                while (reader.TryRead(out var value))
                {
                    Evaluate(value);
                    if (_buffer.Count >= _settings.ArchiveBatchSize)
                    {
                        await FlushAsync(stoppingToken);
                    }
                }

                if (completed == tickTask)
                {
                    ReportBusDrops();
                    EvaluatePeriodic(ScadaTime.NowMs());
                    await FlushAsync(stoppingToken);
                    tickTask = ticker.WaitForNextTickAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }

        await FlushAsync(CancellationToken.None);
    }

    private void Evaluate(TagValue value)
    {
        ReloadConfigIfNeeded();
        if (!_tags.TryGetValue(value.TagId, out var tag) || !IsLocal(tag))
        {
            return;
        }
        if (tag.Kind == Core.Enums.TagKind.Calculated && (value.DefinitionRevision != tag.CalculationRevision || value.Ts <= _config.LoadedAt)) return;

        var last = LastAccepted(value.TagId);
        if (ArchivePolicy.ShouldStore(tag, value, last, value.Ts))
        {
            Buffer(new HistoryValue(value.TagId, value.Ts, value.Value, value.Quality));
        }
    }

    private void EvaluatePeriodic(long now)
    {
        ReloadConfigIfNeeded();
        foreach (var tag in _tags.Values)
        {
            if (tag.ArchiveMode != Core.Enums.ArchiveMode.Periodic || !IsLocal(tag))
            {
                continue;
            }

            var current = _cache.Get(tag.Id);
            if (current is null)
            {
                continue;
            }

            var last = LastAccepted(tag.Id);
            if (ArchivePolicy.ShouldStore(tag, current with { Ts = now }, last, now))
            {
                Buffer(new HistoryValue(tag.Id, now, current.Value, current.Quality));
            }
        }
    }

    private ArchiveState? LastAccepted(int tagId)
    {
        _lastStored.TryGetValue(tagId, out var stored); _lastBuffered.TryGetValue(tagId, out var buffered);
        return buffered is not null && (stored is null || buffered.Ts >= stored.Ts) ? buffered : stored;
    }

    private void Buffer(HistoryValue value)
    {
        _buffer.Add(value); RememberBuffered(value);
    }
    private void RememberBuffered(HistoryValue value)
    {
        if (!_lastBuffered.TryGetValue(value.TagId, out var existing) || value.Ts >= existing.Ts)
            _lastBuffered[value.TagId] = new(value.Ts, value.Value, value.Quality);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            // An idle collector still needs to survive a prolonged database outage.
            await RecoverSpoolAsync(cancellationToken);
            return;
        }

        var snapshot = _buffer.ToList();
        _buffer.Clear();
        _lastBuffered.Clear();
        try
        {
            await DrainSpoolAsync(cancellationToken);
            await _store.WriteRawAsync(snapshot, cancellationToken);
            MarkStored(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "历史数据写入失败，{Count} 条转入磁盘暂存待补传", snapshot.Count);
            try
            {
                // Once removed from the memory buffer, preserve the batch even during shutdown.
                await _spool.AppendAsync(snapshot, CancellationToken.None);
                MarkStored(snapshot);
            }
            catch (Exception spoolException)
            {
                _logger.LogCritical(spoolException, "历史暂存写入失败，{Count} 条只能留在内存等待重试", snapshot.Count);
                _buffer.InsertRange(0, snapshot);
                if (_buffer.Count > _settings.ArchiveBatchSize * 10)
                {
                    var drop = _buffer.Count - _settings.ArchiveBatchSize * 10;
                    _buffer.RemoveRange(0, drop);
                    _logger.LogError("历史内存缓冲超限，丢弃最旧 {Count} 条（数据库与磁盘暂存同时不可写）", drop);
                }
                foreach (var value in _buffer) RememberBuffered(value);
            }
        }
    }

    private Task DrainSpoolAsync(CancellationToken cancellationToken) =>
        _spool.DrainAsync(async batch => { await _store.WriteRawAsync(batch, cancellationToken); MarkStored(batch); }, cancellationToken);

    private async Task RecoverSpoolAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DrainSpoolAsync(cancellationToken);
            if (_spoolFailed) _logger.LogInformation("历史暂存补传已恢复");
            _spoolFailed = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (!_spoolFailed || Environment.TickCount64 - _spoolWarningAt >= 60000)
            {
                _logger.LogWarning(ex, "历史补传未完成，剩余 {Bytes} 字节将在后续周期重试", _spool.PendingBytes);
                _spoolWarningAt = Environment.TickCount64;
            }
            _spoolFailed = true;
        }
    }

    private void ReportBusDrops()
    {
        var total = _bus.DroppedTotal;
        var reported = Interlocked.Exchange(ref _reportedBusDrops, total);
        if (total > reported)
        {
            _logger.LogError("实时总线积压超限，新增丢弃 {Count} 条采集值，累计 {Total} 条", total - reported, total);
        }
    }

    private void MarkStored(IReadOnlyCollection<HistoryValue> values)
    {
        foreach (var item in values)
        {
            if (_lastStored.TryGetValue(item.TagId, out var existing) && existing.Ts > item.Ts)
            {
                continue;
            }

            _lastStored[item.TagId] = new ArchiveState(item.Ts, item.Value, item.Quality);
        }
    }

    private void ReloadConfigIfNeeded()
    {
        if (_config.Version == _configVersion)
        {
            return;
        }

        _tags = _config.Tags.Where(IsLocal).ToDictionary(x => x.Id);
        _configVersion = _config.Version;
    }
    private bool IsLocal(Tag tag) => tag.DeviceId is not int id || !_config.TryGetDevice(id, out var device) || device.GatewayId is null;
}
