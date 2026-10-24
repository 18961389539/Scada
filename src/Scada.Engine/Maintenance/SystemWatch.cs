using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;
using Scada.Engine.History;

namespace Scada.Engine.Maintenance;

public sealed record SystemWarning(string Code, string Message);
public sealed record SystemSnapshot(
    IReadOnlyList<SystemWarning> Warnings, double FreeGb, long HistorySpoolBytes, int BusBacklog, long BusDropped);

// 周期性评估磁盘、历史暂存与实时总线；阈值越界时 /health 转 degraded 并写一次日志。
// 总线丢弃按"本次检查新增"告警：累计值只做展示，否则一次抖动会让状态永远停在 degraded。
public sealed class SystemWatch(
    IOptions<ScadaSettings> settings,
    IRealtimeBus bus,
    HistorySpool spool,
    ILogger<SystemWatch> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

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

        double freeGb = 0;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(options.ConfigDbPath));
            if (!string.IsNullOrEmpty(root)) freeGb = Math.Round(new DriveInfo(root).AvailableFreeSpace / 1024d / 1024d / 1024d, 1);
        }
        catch
        {
            // 拿不到磁盘信息时不编造告警
        }

        var spoolBytes = spool.PendingBytes;
        var backlog = bus.Backlog;
        var dropped = bus.DroppedTotal;
        var droppedDelta = dropped - _lastDropped;

        if (freeGb > 0 && freeGb < options.DiskFreeWarnGb)
        {
            warnings.Add(new("disk_low", $"可用磁盘 {freeGb:F1} GB，低于阈值 {options.DiskFreeWarnGb} GB"));
        }

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
        var snapshot = new SystemSnapshot(warnings, freeGb, spoolBytes, backlog, dropped);
        _current = snapshot;
        _evaluated = true;
        return snapshot;
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
