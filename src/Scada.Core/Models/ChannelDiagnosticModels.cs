namespace Scada.Core.Models;

/// <summary>
/// 共享通道的总线级观测快照。
///
/// 共享总线上真正的稀缺资源是「链路占用时间」而不是连接数：一条 9600 波特率的 RS-485
/// 每秒只能承载有限个事务，任何成员读得过久都会摊薄其它成员的采样周期。
/// 因此这里统计的是排队与占用，而不是吞吐——运维要回答的是「谁在吃总线、吃了多少、有没有人排队」。
/// </summary>
/// <param name="Name">通道名。采集引擎不加载通道主数据，由接口层补齐。</param>
/// <param name="Protocol">通道协议，同样由接口层补齐。</param>
/// <param name="Endpoint">端点指纹（协议 + 主机端口或串口参数），端点变了就是另一条链路。</param>
/// <param name="BusyPercent">自链路建立以来，被事务占用的时间占比。</param>
/// <param name="SlowQueues">排队超过 <see cref="BusPolicies.SlowQueueMs"/> 的事务数，即总线争用次数。</param>
public sealed record ChannelStatistics(
    int ChannelId,
    string Name,
    string Protocol,
    string Endpoint,
    bool Connected,
    int Connections,
    int LeaseCount,
    long Transactions,
    double BusyPercent,
    double WaitAvgMs,
    double WaitMaxMs,
    long SlowQueues,
    string? LastError,
    long CapturedTs,
    IReadOnlyList<ChannelMemberStatistics> Members);

/// <summary>通道上单个成员设备对总线的占用。</summary>
/// <param name="BusySharePercent">该成员占用时间占整条总线事务时间的比例，用来定位「谁把总线吃满了」。</param>
/// <param name="WaitAvgMs">该成员每次事务平均排队时长；持续上升说明总线已经过载。</param>
public sealed record ChannelMemberStatistics(
    int DeviceId,
    string DeviceName,
    int Station,
    long Transactions,
    long SlowQueues,
    long Failures,
    double WaitAvgMs,
    double WaitMaxMs,
    double BusyMs,
    double BusySharePercent,
    long LastTransactionTs);

public static class BusPolicies
{
    /// <summary>排队超过这个时长就记为一次总线争用。低于该值属于正常的调度抖动。</summary>
    public const int SlowQueueMs = 50;

    /// <summary>连接建立预算的下限：再短会把正常的 TLS/串口初始化判成超时。</summary>
    public const int MinConnectBudgetMs = 3_000;

    /// <summary>连接建立预算的上限：不可达端点的 TCP SYN 超时（Windows 约 21s）会把整条总线卡住。</summary>
    public const int MaxConnectBudgetMs = 20_000;

    /// <summary>连接预算 = 设备超时的两倍，并夹取到上下限之间。</summary>
    public static int ConnectBudgetMs(int deviceTimeoutMs) =>
        Math.Clamp(deviceTimeoutMs * 2, MinConnectBudgetMs, MaxConnectBudgetMs);
}
