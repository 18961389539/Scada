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
    private readonly ILogger<HistoryWriterService> _logger;

    private readonly Dictionary<int, ArchiveState> _lastStored = new();
    private readonly List<HistoryValue> _buffer = new();
    private Dictionary<int, Tag> _tags = new();
    private int _configVersion = -1;

    public HistoryWriterService(
        IRealtimeBus bus,
        IRealtimeCache cache,
        IConfigSnapshot config,
        IHistoryStore store,
        IOptions<ScadaSettings> settings,
        ILogger<HistoryWriterService> logger)
    {
        _bus = bus;
        _cache = cache;
        _config = config;
        _store = store;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _bus.Subscribe();
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(_settings.ArchiveFlushIntervalMs, 200)));
        var tickTask = ticker.WaitForNextTickAsync(stoppingToken).AsTask();

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
        if (!_tags.TryGetValue(value.TagId, out var tag))
        {
            return;
        }

        _lastStored.TryGetValue(value.TagId, out var last);
        if (ArchivePolicy.ShouldStore(tag, value, last, value.Ts))
        {
            _buffer.Add(new HistoryValue(value.TagId, value.Ts, value.Value, value.Quality));
        }
    }

    private void EvaluatePeriodic(long now)
    {
        foreach (var tag in _tags.Values)
        {
            if (tag.ArchiveMode != Core.Enums.ArchiveMode.Periodic)
            {
                continue;
            }

            var current = _cache.Get(tag.Id);
            if (current is null)
            {
                continue;
            }

            _lastStored.TryGetValue(tag.Id, out var last);
            if (ArchivePolicy.ShouldStore(tag, current with { Ts = now }, last, now))
            {
                _buffer.Add(new HistoryValue(tag.Id, now, current.Value, current.Quality));
            }
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        var snapshot = _buffer.ToList();
        _buffer.Clear();
        try
        {
            await _store.WriteRawAsync(snapshot, cancellationToken);
            foreach (var item in snapshot)
            {
                _lastStored[item.TagId] = new ArchiveState(item.Ts, item.Value, item.Quality);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "历史数据写入失败，保留 {Count} 条待重试", snapshot.Count);
            _buffer.InsertRange(0, snapshot);
            if (_buffer.Count > _settings.ArchiveBatchSize * 10)
            {
                var drop = _buffer.Count - _settings.ArchiveBatchSize * 10;
                _buffer.RemoveRange(0, drop);
            }
        }
    }

    private void ReloadConfigIfNeeded()
    {
        if (_config.Version == _configVersion)
        {
            return;
        }

        _tags = _config.Tags.ToDictionary(x => x.Id);
        _configVersion = _config.Version;
    }
}
