namespace Scada.Core.Options;

public sealed class GatewaySettings
{
    public bool AllowLoopbackHttp { get; set; }
    public int LeaseSeconds { get; set; } = 30;
    public int CommandTimeoutMs { get; set; } = 10000;
    public int DeviceOfflineDelayMs { get; set; } = 3000;
    public int DeviceStateGraceMs { get; set; } = 15000;
    public long MaxInboxPendingBytes { get; set; } = 512L * 1024 * 1024;
    // 永久性处理失败达到该次数后转入死信，跳过该序号让后续批次继续；临时性错误只退避、不计入死信上限。
    public int MaxInboxAttempts { get; set; } = 10;
    public int InboxRetryBaseMs { get; set; } = 1000;
    public int InboxRetryMaxMs { get; set; } = 60000;
    public const int MaxPacketBytes = 16 * 1024 * 1024;
}
