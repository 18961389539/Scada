using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Scada.Core.Licensing;
using Scada.Core.Options;

namespace Scada.Server.External.Slave;

/// <summary>
/// 对外 Modbus TCP 从站。默认关闭（External:ModbusSlave:Enabled）。
/// 每个连接串行处理请求——Modbus 是严格的请求-响应协议，并发处理同一连接只会让主站的超时判断失真。
/// </summary>
public sealed class ModbusSlaveHost(
    IOptionsMonitor<ExternalIntegrationOptions> options,
    ExportPointMap map,
    ExportWriteRouter router,
    ILicenseEnforcement license,
    ILogger<ModbusSlaveHost> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private TcpListener? _listener;
    // 禁用时不轮询：靠配置变更唤醒。测试宿主会创建大量站点，
    // 每个站点常驻一个 1.5s 定时器会显著抬高并行测试的线程与计时器压力。
    private readonly SemaphoreSlim _configChanged = new(0, 1);
    private bool _licenseWarned;

    public int ConnectionCount => _sessions.Count;
    public bool IsRunning { get; private set; }
    public int MappedPoints => map.Modbus.Count;
    public IReadOnlyList<string> Warnings => map.Warnings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = options.OnChange(_ => SignalConfigChanged());
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue.ModbusSlave;
            if (!opt.Enabled || !Allowed())
            {
                await StopListenerAsync();
                // 许可未放开时仍要定时重试（许可状态不走 options 变更通知）；
                // 纯禁用则完全休眠，等配置热加载唤醒。
                if (opt.Enabled) await Task.Delay(5000, stoppingToken);
                else await WaitForConfigChangeAsync(stoppingToken);
                continue;
            }
            try
            {
                map.Ensure(opt, options.CurrentValue.Iec104Server);
                await AcceptLoopAsync(opt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Modbus 从站监听异常，即将重试");
                await StopListenerAsync();
                await Task.Delay(2000, stoppingToken);
            }
        }
        await StopListenerAsync();
    }

    private void SignalConfigChanged()
    {
        try { _configChanged.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task WaitForConfigChangeAsync(CancellationToken ct)
    {
        try { await _configChanged.WaitAsync(ct); } catch (OperationCanceledException) { }
    }

    private bool Allowed()
    {
        if (license.OutputsAllowed) return true;
        if (!_licenseWarned)
        {
            _licenseWarned = true;
            logger.LogWarning("Modbus 从站已配置但未启用：{Reason}", license.BlockMessage);
        }
        return false;
    }

    private async Task AcceptLoopAsync(ModbusSlaveOptions opt, CancellationToken ct)
    {
        var ip = IPAddress.TryParse(opt.ListenAddress, out var parsed) ? parsed : IPAddress.Any;
        _listener = new TcpListener(ip, opt.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start(Math.Clamp(opt.MaxConnections, 1, 256));
        IsRunning = true;
        logger.LogInformation("Modbus 从站监听 {Addr}:{Port}，映射 {Count} 个地址", opt.ListenAddress, opt.Port, map.Modbus.Count);
        if (map.Warnings.Count > 0)
            foreach (var w in map.Warnings.Take(20)) logger.LogWarning("Modbus 从站点表：{Warning}", w);

        while (!ct.IsCancellationRequested && options.CurrentValue.ModbusSlave.Enabled)
        {
            var client = await _listener.AcceptTcpClientAsync(ct);
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private Task StopListenerAsync()
    {
        IsRunning = false;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        return Task.CompletedTask;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken hostCt)
    {
        var opt = options.CurrentValue.ModbusSlave;
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var remoteIp = remote is null ? "?" : Normalize(remote.Address);
        if (!IsAllowed(opt, remoteIp))
        {
            logger.LogWarning("Modbus 从站拒绝未在白名单的地址 {Ip}", remoteIp);
            client.Dispose();
            return;
        }
        if (_sessions.Count >= Math.Max(1, opt.MaxConnections))
        {
            logger.LogWarning("Modbus 从站连接数已达上限 {Max}，拒绝 {Ip}", opt.MaxConnections, remoteIp);
            client.Dispose();
            return;
        }

        var session = new Session(Guid.NewGuid());
        _sessions[session.Id] = session;
        var stream = client.GetStream();
        var buffer = new byte[1024];
        var length = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(hostCt);
        idle.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(opt.IdleTimeoutSec, 10, 3600)));
        try
        {
            while (!idle.IsCancellationRequested)
            {
                if (length >= buffer.Length) break;
                var read = await stream.ReadAsync(buffer.AsMemory(length, buffer.Length - length), idle.Token);
                if (read <= 0) break;
                length += read;
                idle.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.CurrentValue.ModbusSlave.IdleTimeoutSec, 10, 3600)));

                while (TryTakeFrame(buffer, ref length, out var transactionId, out var unitId, out var pdu, out var protocolError))
                {
                    if (protocolError)
                    {
                        // 协议标识或长度非法：无法安全定位下一帧，只能断开，避免把后续字节解析成伪请求。
                        logger.LogDebug("Modbus 从站收到非法 MBAP，断开 {Ip}", remoteIp);
                        return;
                    }
                    var current = options.CurrentValue.ModbusSlave;
                    if (!UnitAllowed(current, unitId))
                    {
                        await ReplyAsync(stream, transactionId, unitId, ModbusSlaveProtocol.Exception(0x0B, 0x0B), idle.Token);
                        continue;
                    }
                    map.Ensure(current, options.CurrentValue.Iec104Server);
                    var ctx = new ModbusSlaveContext(map, current, router, remoteIp);
                    var response = await ModbusSlaveProtocol.HandleAsync(pdu, ctx, idle.Token);
                    await ReplyAsync(stream, transactionId, unitId, response, idle.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogDebug(ex, "Modbus 从站会话结束 {Id}", session.Id); }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            try { await stream.DisposeAsync(); } catch { }
            try { client.Dispose(); } catch { }
        }
    }

    // MBAP：tid(2) pid(2) len(2) uid(1)，len = 1 + PDU 字节数。
    private static bool TryTakeFrame(byte[] buffer, ref int length, out ushort transactionId, out byte unitId, out byte[] pdu, out bool protocolError)
    {
        transactionId = 0; unitId = 0; pdu = []; protocolError = false;
        if (length < 7) return false;
        var pid = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
        var declared = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4));
        if (pid != 0 || declared is < 2 or > 260)
        {
            protocolError = true;
            return true;
        }
        var frameLength = 6 + declared;
        if (length < frameLength) return false;

        transactionId = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0));
        unitId = buffer[6];
        pdu = buffer.AsSpan(7, declared - 1).ToArray();
        Buffer.BlockCopy(buffer, frameLength, buffer, 0, length - frameLength);
        length -= frameLength;
        return true;
    }

    private static async Task ReplyAsync(Stream stream, ushort transactionId, byte unitId, byte[] pdu, CancellationToken ct)
    {
        var frame = new byte[7 + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame, 7);
        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    private static bool IsAllowed(ModbusSlaveOptions opt, string ip)
    {
        if (opt.IpAllowlist is not { Length: > 0 }) return true;
        return opt.IpAllowlist.Any(a => a == "*" || string.Equals(a, ip, StringComparison.OrdinalIgnoreCase));
    }

    private static bool UnitAllowed(ModbusSlaveOptions opt, byte unitId) =>
        opt.UnitIds is not { Length: > 0 } || opt.UnitIds.Contains(unitId);

    private static string Normalize(IPAddress addr) =>
        addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();

    // 只用于统计在线连接数；会话内的状态都留在栈上，避免为每连接维护可变对象。
    private sealed record Session(Guid Id);
}
