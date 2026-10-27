using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.History;

namespace Scada.Engine.Maintenance;

public sealed record SystemWarning(string Code, string Message);
public sealed record DiskCapacity(string Root, long? TotalBytes, long? FreeBytes);
public sealed record HistoryCapacity(
    DateTimeOffset CheckedAt, long TotalBytes, long RawBytes, long AggregateBytes, long JournalBytes,
    long SpoolBytes, long OtherBytes, int RawFiles, int AggregateFiles, bool ScanComplete, DiskCapacity Disk,
    double? GrowthBytesPerDay, double? DaysUntilDiskWarn,
    int RawRetentionMonths, int FiveMinuteRetentionDays, int HourRetentionDays);
public sealed record SystemSnapshot(
    IReadOnlyList<SystemWarning> Warnings, double FreeGb, long HistorySpoolBytes, int BusBacklog, long BusDropped,
    HistoryCapacity? History = null);

// 周期性评估磁盘、历史暂存与实时总线；阈值越界时 /health 转 degraded 并写一次日志。
// 总线丢弃按"本次检查新增"告警：累计值只做展示，否则一次抖动会让状态永远停在 degraded。
public sealed class SystemWatch(
    IOptions<ScadaSettings> settings,
    IRealtimeBus bus,
    HistorySpool spool,
    ILogger<SystemWatch> logger,
    TimeProvider? timeProvider = null,
    Func<string, DiskCapacity>? diskProbe = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private const double BytesPerGb = 1024d * 1024 * 1024;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Queue<(DateTimeOffset At, long Bytes)> _sizes = new();
    private DateTimeOffset _lastSizeAt;

    private volatile SystemSnapshot _current = new([], 0, 0, 0, 0);
    private readonly object _sync = new();
    private long _lastDropped;
    private string _lastSignature = string.Empty;
    private bool _evaluated;

    // 首个周期之前也要有结果，否则启动后 30 秒内 /health 会声称一切正常。
    public SystemSnapshot Current
    {
        get
        {
            if (!_evaluated) return Evaluate();
            return _current;
        }
    }

    public SystemSnapshot Evaluate()
    {
        lock (_sync)
        {
            return EvaluateCore();
        }
    }

    private SystemSnapshot EvaluateCore()
    {
        var options = settings.Value;
        var warnings = new List<SystemWarning>();

        var configDisk = ProbeDisk(options.ConfigDbPath);
        var freeGb = Math.Round((configDisk.FreeBytes ?? 0) / BytesPerGb, 1);
        var history = MeasureHistory(options);

        var spoolBytes = spool.PendingBytes;
        var backlog = bus.Backlog;
        var dropped = bus.DroppedTotal;
        var droppedDelta = dropped - _lastDropped;

        if (options.DiskFreeWarnGb > 0 && configDisk.FreeBytes is { } configFree && configFree / BytesPerGb < options.DiskFreeWarnGb)
        {
            warnings.Add(new("disk_low", $"可用磁盘 {freeGb:F1} GB，低于阈值 {options.DiskFreeWarnGb} GB"));
        }

        if (history.Disk.FreeBytes is null) warnings.Add(new("history_disk_unavailable", "无法读取历史目录所在磁盘的容量"));
        else if (options.DiskFreeWarnGb > 0 && history.Disk.FreeBytes.Value / BytesPerGb < options.DiskFreeWarnGb)
            warnings.Add(new("history_disk_low", $"历史磁盘可用 {history.Disk.FreeBytes.Value / BytesPerGb:F1} GB，低于阈值 {options.DiskFreeWarnGb} GB"));
        if (!history.ScanComplete) warnings.Add(new("history_capacity_unavailable", "历史目录容量扫描不完整，请检查目录权限和磁盘状态"));
        if (options.HistorySizeWarnGb > 0 && history.TotalBytes / BytesPerGb >= options.HistorySizeWarnGb)
            warnings.Add(new("history_size_high", $"历史目录占用 {history.TotalBytes / BytesPerGb:F2} GB，达到阈值 {options.HistorySizeWarnGb} GB"));
        if (options.HistoryCapacityWarnDays > 0 && history.DaysUntilDiskWarn is { } days && days <= options.HistoryCapacityWarnDays)
            warnings.Add(new("history_capacity_forecast", $"按近期历史净增长估算，约 {days:F1} 天后达到磁盘告警阈值"));

        if (spoolBytes > options.HistorySpoolWarnBytes)
        {
            warnings.Add(new("history_spool_backlog", $"历史暂存积压 {spoolBytes / 1024} KB，超过阈值 {options.HistorySpoolWarnBytes / 1024} KB，说明写库持续失败"));
        }

        if (backlog > options.BusBacklogWarn)
        {
            warnings.Add(new("bus_backlog", $"实时总线积压 {backlog} 条，超过阈值 {options.BusBacklogWarn} 条"));
        }

        if (droppedDelta > 0)
        {
            warnings.Add(new("bus_dropped", $"本次检查新增丢弃 {droppedDelta} 条采集值（累计 {dropped}），历史与报警可能缺数据"));
        }

        _lastDropped = dropped;
        var snapshot = new SystemSnapshot(warnings, freeGb, spoolBytes, backlog, dropped, history);
        _current = snapshot;
        _evaluated = true;
        return snapshot;
    }

    private DiskCapacity ProbeDisk(string path)
    {
        try
        {
            if (diskProbe is not null) return diskProbe(path);
            var fullPath = Path.GetFullPath(path);
            var root = DriveInfo.GetDrives().Where(drive => fullPath.StartsWith(drive.Name,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(drive => drive.Name.Length).FirstOrDefault()?.Name
                ?? Path.GetPathRoot(fullPath) ?? fullPath;
            var info = new DriveInfo(root);
            return new DiskCapacity(root, info.TotalSize, info.AvailableFreeSpace);
        }
        catch { return new DiskCapacity(string.Empty, null, null); }
    }

    private HistoryCapacity MeasureHistory(ScadaSettings options)
    {
        long raw = 0, aggregate = 0, journal = 0, spoolSize = 0, other = 0;
        var rawFiles = 0;
        var aggregateFiles = 0;
        var complete = true;
        try
        {
            if (Directory.Exists(options.HistoryRoot))
            {
                foreach (var path in Directory.EnumerateFiles(options.HistoryRoot, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var file = new FileInfo(path);
                        var bytes = file.Length;
                        var relative = Path.GetRelativePath(options.HistoryRoot, path);
                        if (relative.StartsWith("_spool" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) spoolSize += bytes;
                        else if (file.Name.EndsWith(".db-wal", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".db-shm", StringComparison.OrdinalIgnoreCase)) journal += bytes;
                        else if (file.Name.StartsWith("raw_", StringComparison.OrdinalIgnoreCase) && file.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase)) { raw += bytes; rawFiles++; }
                        else if (file.Name.StartsWith("agg_", StringComparison.OrdinalIgnoreCase) && file.Extension.Equals(".db", StringComparison.OrdinalIgnoreCase)) { aggregate += bytes; aggregateFiles++; }
                        else other += bytes;
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    catch (IOException) { complete = false; }
                    catch (UnauthorizedAccessException) { complete = false; }
                }
            }
        }
        catch (IOException) { complete = false; }
        catch (UnauthorizedAccessException) { complete = false; }
        var total = raw + aggregate + journal + spoolSize + other;
        var now = _clock.GetUtcNow();
        double? growth = null;
        if (complete)
        {
            while (_sizes.Count > 1 && now - _sizes.Peek().At > TimeSpan.FromHours(24)) _sizes.Dequeue();
            if (_sizes.Count == 0 || now - _lastSizeAt >= Interval)
            {
                _sizes.Enqueue((now, total));
                _lastSizeAt = now;
            }
            var first = _sizes.Peek();
            var elapsed = now - first.At;
            if (elapsed >= TimeSpan.FromMinutes(5)) growth = Math.Max(0, (total - first.Bytes) / elapsed.TotalDays);
        }
        var disk = ProbeDisk(options.HistoryRoot);
        double? days = complete && disk.FreeBytes is { } free && growth is > 0
            ? Math.Max(0, free - Math.Max(0, options.DiskFreeWarnGb) * BytesPerGb) / growth.Value : null;
        return new HistoryCapacity(now, total, raw, aggregate, journal, spoolSize, other, rawFiles, aggregateFiles, complete,
            disk, growth, days, Math.Max(1, options.HistoryRetentionMonths), Math.Max(1, options.Agg5MinRetentionDays), Math.Max(0, options.AggHourRetentionDays));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Report(Evaluate());
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Report(Evaluate());
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "运维状态检查失败，下个周期重试");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private void Report(SystemSnapshot snapshot)
    {
        var signature = string.Join("|", snapshot.Warnings.Select(x => x.Code).Order(StringComparer.Ordinal));
        if (signature == _lastSignature) return;

        foreach (var warning in snapshot.Warnings)
        {
            logger.LogWarning("运维告警[{Code}] {Message}", warning.Code, warning.Message);
        }

        if (snapshot.Warnings.Count == 0 && _lastSignature.Length > 0)
        {
            logger.LogInformation("运维告警已恢复：磁盘 {FreeGb} GB，历史暂存 {SpoolBytes} 字节，总线积压 {Backlog}", snapshot.FreeGb, snapshot.HistorySpoolBytes, snapshot.BusBacklog);
        }

        _lastSignature = signature;
    }
}
