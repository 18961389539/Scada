using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Scada.Core.Licensing;
using Scada.Core.Net;
using Scada.Core.Options;

namespace Scada.Server.External.Slave;

/// <summary>
/// 对外 IEC 60870-5-104 从站（被控站）。默认关闭（External:Iec104Server:Enabled）。
/// 与 Modbus 从站共用同一份点表映射和控制写回路径，只是对外协议不同。
/// </summary>
public sealed class Iec104ServerHost(
    IOptionsMonitor<ExternalIntegrationOptions> options,
    ExportPointMap map,
    ExportWriteRouter router,
    ILicenseEnforcement license,
    ILogger<Iec104ServerHost> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, Iec104ServerSession> _sessions = new();
    private TcpListener? _listener;
    // 禁用时不轮询：靠配置变更唤醒。测试宿主会创建大量站点，
    // 每个站点常驻一个 1.5s 定时器会显著抬高并行测试的线程与计时器压力。
    private readonly SemaphoreSlim _configChanged = new(0, 1);
    private bool _licenseWarned;

    public int ConnectionCount => _sessions.Count;
    public bool IsRunning { get; private set; }
    public int MappedPoints => map.Iec.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = options.OnChange(_ => SignalConfigChanged());
        while (!stoppingToken.IsCancellationRequested)
        {
            var opt = options.CurrentValue.Iec104Server;
            if (!opt.Enabled || !Allowed())
            {
                await StopListenerAsync();
                if (opt.Enabled) await Task.Delay(5000, stoppingToken);
                else await WaitForConfigChangeAsync(stoppingToken);
                continue;
            }
            try
            {
                map.Ensure(options.CurrentValue.ModbusSlave, opt);
                await AcceptLoopAsync(opt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "IEC 104 从站监听异常，即将重试");
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
            logger.LogWarning("IEC 104 从站已配置但未启用：{Reason}", license.BlockMessage);
        }
        return false;
    }

    private async Task AcceptLoopAsync(Iec104ServerOptions opt, CancellationToken ct)
    {
        var ip = IPAddress.TryParse(opt.ListenAddress, out var parsed) ? parsed : IPAddress.Any;
        _listener = new TcpListener(ip, opt.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _listener.Start(Math.Clamp(opt.MaxConnections, 1, 64));
        IsRunning = true;
        logger.LogInformation("IEC 104 从站监听 {Addr}:{Port}，点表 {Count} 个 IOA，公共地址 {Ca}",
            opt.ListenAddress, opt.Port, map.Iec.Count, string.Join(',', opt.CommonAddresses));

        while (!ct.IsCancellationRequested && options.CurrentValue.Iec104Server.Enabled)
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
        var opt = options.CurrentValue.Iec104Server;
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var remoteIp = remote is null ? "?" : Normalize(remote.Address);
        if (!IpRule.IsAllowed(remote?.Address, opt.IpAllowlist))
        {
            logger.LogWarning("IEC 104 从站拒绝来源 {Ip}：未配置白名单或不在名单内", remoteIp);
            client.Dispose();
            return;
        }
        if (_sessions.Count >= Math.Max(1, opt.MaxConnections))
        {
            logger.LogWarning("IEC 104 从站连接数已达上限 {Max}，拒绝 {Ip}", opt.MaxConnections, remoteIp);
            client.Dispose();
            return;
        }

        var commonAddress = opt.CommonAddresses is { Length: > 0 } ? opt.CommonAddresses[0] : 1;
        await using var session = new Iec104ServerSession(client.GetStream(), opt, map, router, logger, remoteIp, commonAddress);
        var id = Guid.NewGuid();
        _sessions[id] = session;
        try
        {
            logger.LogInformation("IEC 104 从站接受连接 {Ip}", remoteIp);
            await session.RunAsync(hostCt);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "IEC 104 从站会话结束 {Ip}", remoteIp);
        }
        finally
        {
            _sessions.TryRemove(id, out _);
            try { client.Dispose(); } catch { }
            logger.LogInformation("IEC 104 从站断开 {Ip}", remoteIp);
        }
    }

    private static string Normalize(IPAddress addr) =>
        addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
}
