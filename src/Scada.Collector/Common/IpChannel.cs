using System.Net.Sockets;

namespace Scada.Collector.Common;

/// <summary>请求-应答型 TCP/UDP 通道：一次只有一个在途请求，超时即断开（避免迟到应答错配）。</summary>
public sealed class IpChannel : IAsyncDisposable
{
    private readonly TcpClient? _tcp;
    private readonly NetworkStream? _stream;
    private readonly UdpClient? _udp;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public bool IsOpen { get; private set; }

    private IpChannel(TcpClient tcp) { _tcp = tcp; _stream = tcp.GetStream(); IsOpen = true; }
    private IpChannel(UdpClient udp) { _udp = udp; IsOpen = true; }

    public static async Task<IpChannel> OpenAsync(string host, int port, bool udp, int timeoutMs, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
        try
        {
            if (udp)
            {
                var client = new UdpClient();
                try { client.Connect(host, port); } catch { client.Dispose(); throw; }
                return new IpChannel(client);
            }
            var tcp = new TcpClient { NoDelay = true };
            try { await tcp.ConnectAsync(host, port, deadline.Token); }
            catch { tcp.Dispose(); throw; }
            return new IpChannel(tcp);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"连接 {host}:{port} 超时"); }
    }

    /// <summary>发送请求并读取一个完整应答。<paramref name="frameLength"/> 根据已收字节返回完整帧长度（未知返回 null）。</summary>
    public async Task<byte[]> RequestAsync(byte[] request, Func<ReadOnlySpan<byte>, int?> frameLength, int timeoutMs, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeoutMs);
        try
        {
            if (_udp is not null)
            {
                while (_udp.Available > 0) { var stale = new System.Net.IPEndPoint(0, 0); _udp.Receive(ref stale); } // 丢弃上次超时的迟到应答
                await _udp.SendAsync(request, deadline.Token);
                var result = await _udp.ReceiveAsync(deadline.Token);
                return result.Buffer;
            }
            await _stream!.WriteAsync(request, deadline.Token);
            var buffer = new byte[8192]; var count = 0;
            while (true)
            {
                var need = frameLength(buffer.AsSpan(0, count));
                if (need is int n && count >= n) return buffer[..n];
                if (need is int m && m > buffer.Length) Array.Resize(ref buffer, m);
                var read = await _stream.ReadAsync(buffer.AsMemory(count), deadline.Token);
                if (read == 0) throw new IOException("对端关闭了连接");
                count += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Close(); throw new TimeoutException("等待应答超时"); }
        catch (Exception ex) when (ex is IOException or SocketException) { Close(); throw; }
        finally { _lock.Release(); }
    }

    public void Close() { IsOpen = false; _stream?.Dispose(); _tcp?.Dispose(); _udp?.Dispose(); }
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}
