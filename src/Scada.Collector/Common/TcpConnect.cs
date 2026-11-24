using System.Net.Sockets;

namespace Scada.Collector.Common;

/// <summary>
/// 可取消、遵守设备超时的 TCP 连接。与 <see cref="IpChannel.OpenAsync"/> 的 TCP 路径相同：
/// 用 <see cref="CancellationTokenSource.CancelAfter(int)"/> 限制 ConnectAsync，超时转为 TimeoutException，失败时释放套接字。
/// </summary>
public static class TcpConnect
{
    public static async Task<TcpClient> ConnectAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeoutMs);
        try
        {
            await client.ConnectAsync(host, port, deadline.Token);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException($"连接 {host}:{port} 超时");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
