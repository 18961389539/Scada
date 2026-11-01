using Scada.Core.Entities;
using Scada.Core.Protocols;

namespace Scada.Collector.Mitsubishi;

/// <summary>
/// MC 3E 传输通道：只负责连接与"一请求一应答"的原始帧收发；
/// 帧编解码、结束码与批量语义仍由 <see cref="McFrame"/> 负责，两条通道行为一致。
/// </summary>
internal interface IMcChannel
{
    bool IsOpen { get; }
    Task OpenAsync(Device device, McDeviceOptions options, CancellationToken ct);
    Task<byte[]> RequestAsync(byte[] request, Func<ReadOnlySpan<byte>, int?> frameLength, int timeoutMs, CancellationToken ct);
    void Close();
}
