using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Collector.Common;
using Scada.Collector.Modbus;
using Scada.Core.Entities;

namespace Scada.Collector.Tests;

public class TcpConnectTests
{
    [Fact]
    public async Task ConnectTimeoutReturnsWithinTheDeadline()
    {
        await using var hole = await ConnectBlackHole.StartAsync();
        var stamp = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => TcpConnect.ConnectAsync(hole.Host, hole.Port, timeoutMs: 400, CancellationToken.None));
        stamp.Stop();
        Assert.Contains("超时", error.Message);
        Assert.InRange(stamp.ElapsedMilliseconds, 200, 2_000);
    }

    [Fact]
    public async Task ConnectCancellationReturnsWithinTheDeadline()
    {
        await using var hole = await ConnectBlackHole.StartAsync();
        using var cancel = new CancellationTokenSource(200);
        var stamp = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TcpConnect.ConnectAsync(hole.Host, hole.Port, timeoutMs: 30_000, cancel.Token));
        stamp.Stop();
        Assert.True(stamp.ElapsedMilliseconds < 2_000, $"取消后仍等待了 {stamp.Elapsed}");
    }

    [Fact]
    public async Task ConnectSucceedsAgainstALocalListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync();
        try
        {
            using var client = await TcpConnect.ConnectAsync("127.0.0.1", port, timeoutMs: 2000, CancellationToken.None);
            Assert.True(client.Connected);
            using var peer = await accept;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ModbusTcpConnectTimeoutReturnsWithinTheDeviceTimeout()
    {
        await using var hole = await ConnectBlackHole.StartAsync();
        var device = Device(hole.Port, timeoutMs: 400);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);
        var stamp = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(device));
        stamp.Stop();
        Assert.False(driver.IsConnected);
        Assert.Contains("超时", error.Message);
        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.InRange(stamp.ElapsedMilliseconds, 200, 2_000);
    }

    [Fact]
    public async Task ModbusTcpConnectCancellationReturnsPromptly()
    {
        await using var hole = await ConnectBlackHole.StartAsync();
        var device = Device(hole.Port, timeoutMs: 30_000);
        await using var driver = new ModbusTcpDriver(NullLogger<ModbusTcpDriver>.Instance);
        using var cancel = new CancellationTokenSource(200);
        var stamp = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.ConnectAsync(device, cancel.Token));
        stamp.Stop();
        Assert.False(driver.IsConnected);
        Assert.True(stamp.ElapsedMilliseconds < 2_000, $"取消后仍等待了 {stamp.Elapsed}");
    }

    private static Device Device(int port, int timeoutMs) => new()
    {
        Id = 1,
        Name = "blackhole",
        Host = "127.0.0.1",
        Port = port,
        StationNumber = 1,
        TimeoutMs = timeoutMs,
        Enabled = true
    };

    /// <summary>
    /// 本机把监听队列塞满后，再向同一端口发起的连接会一直停在 SYN，用来模拟防火墙丢包或对端不应答。
    /// </summary>
    private sealed class ConnectBlackHole : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _held = [];

        public string Host { get; } = "127.0.0.1";
        public int Port { get; }

        private ConnectBlackHole(TcpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public static async Task<ConnectBlackHole> StartAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(1);
            var hole = new ConnectBlackHole(listener, ((IPEndPoint)listener.LocalEndpoint).Port);
            try
            {
                for (var i = 0; i < 256; i++)
                {
                    var probe = new TcpClient { NoDelay = true };
                    var connect = probe.ConnectAsync(IPAddress.Loopback, hole.Port);
                    var winner = await Task.WhenAny(connect, Task.Delay(80));
                    hole._held.Add(probe);
                    if (winner != connect) return hole;
                    await connect;
                }
                throw new InvalidOperationException("无法把本机监听队列塞满以模拟连接挂起");
            }
            catch
            {
                await hole.DisposeAsync();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            try { _listener.Stop(); } catch { /* 测试清理 */ }
            foreach (var client in _held)
            {
                try { client.Dispose(); } catch { /* 测试清理 */ }
            }
            return ValueTask.CompletedTask;
        }
    }
}
