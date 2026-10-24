namespace Scada.Core.Options;

public sealed class GatewaySettings
{
    public bool AllowLoopbackHttp { get; set; }
    public int LeaseSeconds { get; set; } = 30;
    public long MaxInboxPendingBytes { get; set; } = 512L * 1024 * 1024;
    public const int MaxPacketBytes = 16 * 1024 * 1024;
}
