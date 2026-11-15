using System.Threading;

namespace Scada.Data;

/// <summary>
/// 启动阶段配置库迁移的运行数据。以前只写日志（"应用 N 个迁移，耗时 X 秒"），
/// 日志会被滚动清掉，无法回答"这次升级是不是比上次慢了一倍""迁移链是不是又长了"。
/// 这里把同一次启动的四个数字留成进程内快照，供 <c>/metrics</c> 抓取。
///
/// 注意语义：这些是 <b>gauge</b> 不是 counter——每次进程启动都会重新记一次，
/// 重启后归零是正常的；要长期趋势请按时间序列存 Prometheus，不要在应用里累加。
/// </summary>
public sealed class MigrationTelemetry
{
    private int _recorded;

    /// <summary>本次启动实际应用的迁移条数（新装 = 全链，升级 = 增量，已是最新 = 0）。</summary>
    public int Applied { get; private set; }

    /// <summary>本次启动前待应用的迁移条数，等于 Applied（失败场景除外）。</summary>
    public int Pending { get; private set; }

    /// <summary>迁移完成后配置库 __EFMigrationsHistory 里的总条数。</summary>
    public int Total { get; private set; }

    /// <summary>迁移耗时（秒）。</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>是否已完成本轮记录。未跑过 InitializeAsync 的宿主（纯工具进程）为 false。</summary>
    public bool Recorded => Volatile.Read(ref _recorded) != 0;

    /// <summary>记录时间（UTC），用于判断抓到的是不是本次启动的数据。</summary>
    public DateTimeOffset RecordedAt { get; private set; }

    public void Record(int pending, int applied, int total, double durationSeconds)
    {
        // 用 volatile 做一次发布：读者看到 Recorded=true 时，前面的字段一定已经写完。
        Pending = pending;
        Applied = applied;
        Total = total;
        DurationSeconds = durationSeconds;
        RecordedAt = DateTimeOffset.UtcNow;
        Volatile.Write(ref _recorded, 1);
    }
}
