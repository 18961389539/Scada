using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Health;

namespace Scada.Engine.Maintenance;

/// <summary>
/// 系统时钟偏差检测。SCADA 的所有结论都建立在时间戳上：采集值、报警先后、OEE 班次归属、HA 租约
/// 都依赖本地时钟。<c>HaOptions.ClockSkewMs</c> 的注释写着"需 NTP 对时"，但应用从不校验——
/// 现场时钟漂到几十秒时，表现出来是"历史曲线错位""报警顺序颠倒"，排查方向却往往先怀疑采集。
///
/// 用 SNTP（UDP 123）向配置的服务器取一次时间，只检测并告警，<b>不修改系统时钟</b>：
/// 应用既没有权限也不应该去调系统时间，对时交给 W32Time / chrony。
/// 结果通过 <see cref="IHealthContributor"/> 进入 /health，与其它系统告警同一处可见。
/// </summary>
public sealed class ClockSkewMonitor(
    IOptions<ScadaSettings> options,
    ILogger<ClockSkewMonitor> logger) : BackgroundService, IHealthContributor
{
    private readonly ScadaSettings _settings = options.Value;
    private volatile SystemWarning? _warning;

    public IEnumerable<SystemWarning> HealthWarnings() => _warning is { } warning ? [warning] : [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.ClockCheckEnabled) return;

        // 启动后先等一会：宿主启动阶段网络栈与服务注册未必就绪，而且启动日志已经够长，不必挤在一起。
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var interval = TimeSpan.FromMinutes(Math.Clamp(_settings.ClockCheckIntervalMinutes, 5, 1440));
        using var timer = new PeriodicTimer(interval);
        do
        {
            await CheckAsync(stoppingToken);
        }
        while (await WaitNextAsync(timer, stoppingToken));
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var skew = await QuerySkewAsync(_settings.ClockCheckServer, cancellationToken);
            if (skew is null)
            {
                // 拿不到时间（无外网、UDP 被策略拦、DNS 失败）不代表时钟有问题：
                // 只在 Debug 留痕，不产生健康告警，避免无外网现场长期亮红。
                logger.LogDebug("时钟校验不可用：无法从 {Server} 获取时间", _settings.ClockCheckServer);
                return;
            }

            var ms = (long)skew.Value.TotalMilliseconds;
            var threshold = Math.Max(1000, _settings.ClockSkewWarnMs);
            if (Math.Abs(ms) < threshold)
            {
                if (_warning is not null)
                {
                    logger.LogInformation("系统时钟偏差已恢复：{SkewMs} ms", ms);
                    _warning = null;
                }
                return;
            }

            var direction = ms > 0 ? "快" : "慢";
            var message = $"系统时钟比 {_settings.ClockCheckServer} {direction} {Math.Abs(ms) / 1000.0:0.#} 秒" +
                          "；请检查 W32Time / chrony 对时，否则历史曲线、报警先后与班次归属都会错位";
            _warning = new SystemWarning("clock_skew", message);
            logger.LogWarning("检测到系统时钟偏差：{SkewMs} ms（阈值 {ThresholdMs} ms）", ms, threshold);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 正常停机
        }
        catch (Exception ex)
        {
            // 检测本身失败不是系统故障：DNS/网络/防火墙策略都会走到这里，保持静默以免刷屏。
            logger.LogDebug(ex, "时钟校验失败");
        }
    }

    private static async Task<bool> WaitNextAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try { return await timer.WaitForNextTickAsync(cancellationToken); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>返回"本机时间 − 服务器时间"，正数表示本机走快了。失败返回 null。</summary>
    private static async Task<TimeSpan?> QuerySkewAsync(string server, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server)) return null;

        var addresses = await Dns.GetHostAddressesAsync(server, cancellationToken);
        var address = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses.FirstOrDefault();
        if (address is null) return null;

        using var udp = new UdpClient(address.AddressFamily);
        // SNTP 请求头 48 字节：LI=0、VN=3、Mode=3（客户端）；其余字段留 0，服务器会回填发送时间戳。
        var request = new byte[48];
        request[0] = 0x1B;
        await udp.SendAsync(request, new IPEndPoint(address, 123), cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var response = await udp.ReceiveAsync(timeout.Token);
        var buffer = response.Buffer;
        if (buffer.Length < 48) return null;

        // 服务器"发送时间戳"在字节 40..47：NTP 纪元（1900-01-01）起的 32.32 定点秒。
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(40, 4));
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(44, 4));
        if (seconds == 0) return null;

        var serverTime = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddSeconds(seconds)
            .AddTicks((long)(fraction * (TimeSpan.TicksPerSecond / 4294967296.0)));
        // 未扣除网络往返的一半（通常 <100ms），对秒级阈值没有影响。
        return DateTimeOffset.UtcNow - serverTime;
    }
}
