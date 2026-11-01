using Scada.Collector.Common;
using Scada.Core.Entities;
using Scada.Core.Protocols;

namespace Scada.Collector.Mitsubishi;

/// <summary>自研通道：TCP/UDP 由 <see cref="IpChannel"/> 收发，支持二进制与 ASCII 3E 帧。</summary>
internal sealed class NativeMcChannel : IMcChannel
{
    private IpChannel? _channel;
    public bool IsOpen => _channel?.IsOpen == true;

    public async Task OpenAsync(Device device, McDeviceOptions options, CancellationToken ct)
    {
        Close();
        _channel = await IpChannel.OpenAsync(device.Host, device.Port, options.Transport == IpTransport.Udp, device.TimeoutMs, ct);
    }

    public Task<byte[]> RequestAsync(byte[] request, Func<ReadOnlySpan<byte>, int?> frameLength, int timeoutMs, CancellationToken ct)
        => (_channel ?? throw new IOException("MC 尚未连接")).RequestAsync(request, frameLength, timeoutMs, ct);

    public void Close() { _channel?.Close(); _channel = null; }
}
