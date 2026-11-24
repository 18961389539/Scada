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
    private readonly HistoryLossLog? _lossLog;
    private readonly ILogger<HistoryWriterService> _logger;
    private readonly LivenessRegistry? _liveness;
    private long _reportedHistoryRejected;
    private long _reportedHistoryGapRejected;
    private long _gapMarkFrom;
    private long _gapMarkTo;
    private int _gapMarkCount;
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
        ILogger<HistoryWriterService> logger,
        LivenessRegistry? liveness = null,
        HistoryLossLog? lossLog = null)
    {
        _liveness = liveness;
        _bus = bus;
        _cache = cache;
        _config = config;
        _store = store;
        _settings = settings.Value;
        _spool = spool;
        _lossLog = lossLog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _bus.SubscribeHistory(out var reader);
        using var gapSubscription = _bus.SubscribeHistoryGaps(out var gapReader);
        using var ticker = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(_settings.ArchiveFlushIntervalMs, 200)));
        var tickTask = ticker.WaitForNextTickAsync(stoppingToken).AsTask();

        await RecoverSpoolAsync(stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _liveness?.Beat("history-writer");
                ReloadConfigIfNeeded();
                var dataTask = reader.WaitToReadAsync(stoppingToken).AsTask();
                var gapTask = gapReader.WaitToReadAsync(stoppingToken).AsTask();
                var completed = await Task.WhenAny(tickTask, dataTask, gapTask);

                while (reader.TryRead(out var value))
                {
                    Evaluate(value);
                    if (_buffer.Count >= _settings.ArchiveBatchSize)
                    {
                        await FlushAsync(stoppingToken);
                    }
                }
                while (gapReader.TryRead(out var gap))
                {
                    NoteGapMark(gap.Ts);
                    Evaluate(gap);
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
            Buffer(new HistoryValue(value.TagId, value.Ts, value.Value, value.Quality, value.Text));
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
                Buffer(new HistoryValue(tag.Id, now, current.Value, current.Quality, current.Text));
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
            _lastBuffered[value.TagId] = new(value.Ts, value.Value, value.Quality, value.Text);
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
                _logger.LogCritical(spoolException, "历史暂存写入失败，{Count} 条留在内存等待重试，不会丢弃", snapshot.Count);
                _buffer.InsertRange(0, snapshot);
                if (_buffer.Count > _settings.ArchiveBatchSize * 10)
                    _logger.LogCritical("历史内存缓冲已超过 {Limit} 条，数据库与磁盘暂存同时不可写，样本仍保留在内存", _settings.ArchiveBatchSize * 10);
                foreach (var value in _buffer) RememberBuffered(value);
            }
        }
    }

    private Task DrainSpoolAsync(CancellationToken cancellationToken) =>
        _spool.DrainAsync(async batch => { await _store.WriteRawAsync(batch, cancellationToken); MarkStored(batch); }, cancellationToken);

    public async Task RecoverSpoolAsync(CancellationToken cancellationToken)
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

    internal void NoteGapMark(long ts)
    {
        if (_gapMarkCount == 0)
        {
            _gapMarkFrom = _gapMarkTo = ts;
        }
        else
        {
            if (ts < _gapMarkFrom) _gapMarkFrom = ts;
            if (ts > _gapMarkTo) _gapMarkTo = ts;
        }
        _gapMarkCount++;
    }

    // 只把历史订阅拒收与缺口标记队列溢出记入损失日志；界面订阅积压（DroppedTotal）不计入历史缺口。
    // 窗口优先用已读到的缺口标记时间戳；标记队列本身溢出时没有时间戳，才退回本次冲洗周期。
    internal void ReportBusDrops()
    {
        var rejected = _bus.HistoryRejected;
        var prevRejected = Interlocked.Exchange(ref _reportedHistoryRejected, rejected);
        var gapRejected = _bus.HistoryGapRejected;
        var prevGapRejected = Interlocked.Exchange(ref _reportedHistoryGapRejected, gapRejected);
        var now = ScadaTime.NowMs();
        var flush = Math.Max(_settings.ArchiveFlushIntervalMs, 200);
        if (rejected > prevRejected)
        {
            var from = _gapMarkCount > 0 ? _gapMarkFrom : now - flush;
            var to = _gapMarkCount > 0 ? _gapMarkTo : now;
            _lossLog?.Record(HistoryGapKind.Rejected, from, to,
                detail: $"总线拒收 {rejected - prevRejected} 条历史样本");
            _logger.LogError("历史订阅队列已满，新增拒收 {Count} 条，累计 {Total} 条", rejected - prevRejected, rejected);
        }
        if (gapRejected > prevGapRejected)
        {
            _lossLog?.Record(HistoryGapKind.Rejected, now - flush, now,
                detail: $"缺口标记队列溢出 {gapRejected - prevGapRejected} 条");
            _logger.LogError("历史缺口标记队列已满，新增丢弃 {Count} 条，累计 {Total} 条", gapRejected - prevGapRejected, gapRejected);
        }
        _gapMarkCount = 0;
    }

    private void MarkStored(IReadOnlyCollection<HistoryValue> values)
    {
        foreach (var item in values)
        {
            if (_lastStored.TryGetValue(item.TagId, out var existing) && existing.Ts > item.Ts)
            {
                continue;
            }

            _lastStored[item.TagId] = new ArchiveState(item.Ts, item.Value, item.Quality, item.Text);
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
