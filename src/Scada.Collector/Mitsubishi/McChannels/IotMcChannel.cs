using IoTClient.Clients.PLC;
using IoTClient.Enums;
using Scada.Core.Entities;
using Scada.Core.Protocols;

namespace Scada.Collector.Mitsubishi;

/// <summary>
/// IoTClient 通道：仅用其 TCP 传输（<c>SendPackageSingle</c>）收发原始 3E 二进制帧。
/// 不使用高层 Read/Write：它们会吞掉结束码与超时；帧编解码仍由 <see cref="McFrame"/> 完成，
/// 因此支持全部软元件、结束码隔离与既有批量语义。仅支持 TCP + 二进制。
/// </summary>
internal sealed class IotMcChannel : IMcChannel
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MitsubishiClient? _client;

    public bool IsOpen => _client?.Connected == true;

    public async Task OpenAsync(Device device, McDeviceOptions options, CancellationToken ct)
    {
        Close();
        if (options.Format != McFormat.Binary || options.Transport != IpTransport.Tcp)
            throw new NotSupportedException("IoTClient 引擎仅支持 TCP + 二进制 3E");

        var client = new MitsubishiClient(MitsubishiVersion.Qna_3E, device.Host, device.Port, device.TimeoutMs);
        try
        {
            await Task.Run(client.Open, ct).WaitAsync(TimeSpan.FromMilliseconds(Math.Max(device.TimeoutMs, 1000)), ct);
            _client = client;
        }
        catch (Exception ex)
        {
            try { client.Close(); } catch { }
            throw ex is OperationCanceledException ? ex : new IOException($"连接 {device.Host}:{device.Port} 失败：{ex.Message}", ex);
        }
    }

    public async Task<byte[]> RequestAsync(byte[] request, Func<ReadOnlySpan<byte>, int?> frameLength, int timeoutMs, CancellationToken ct)
    {
        var client = _client ?? throw new IOException("MC 尚未连接");
        await _gate.WaitAsync(ct);
        try
        {
            // IoTClient 为同步 API：放到线程池执行，并用 WaitAsync 兜底，避免阻塞采集循环。
            var result = await Task.Run(() => client.SendPackageSingle(request), ct)
                .WaitAsync(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1000) + 1000), ct);
            if (!result.IsSucceed || result.Value is not { Length: > 0 })
            {
                Close();
                throw new IOException(result.Err ?? "IoTClient 未返回 MC 应答");
            }
            return result.Value;
        }
        catch (Exception ex)
        {
            Close();
            if (ex is OperationCanceledException) throw;
            throw ex is IOException ? ex : new IOException(ex.Message, ex);
        }
        finally { _gate.Release(); }
    }

    public void Close()
    {
        var client = _client; _client = null;
        try { client?.Close(); } catch { }
    }
}
