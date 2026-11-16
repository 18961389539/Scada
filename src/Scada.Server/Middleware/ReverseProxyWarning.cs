namespace Scada.Server.Middleware;

/// <summary>
/// 代理提示：请求带 <c>X-Forwarded-For</c> 但未启用 <c>Scada:ReverseProxy</c> 时，
/// 说明前面有代理，而限流与 IP 白名单只能看到代理地址——几十台终端会被合并成一个配额桶。
/// 每 5 分钟最多提示一次，避免每请求刷日志。
/// </summary>
public sealed class ReverseProxyWarning(RequestDelegate next, ILogger<ReverseProxyWarning> logger)
{
    private static long _lastWarnTs;

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("X-Forwarded-For")) WarnOnce();
        await next(context);
    }

    private void WarnOnce()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastWarnTs);
        if (now - last < 300_000) return;
        if (Interlocked.CompareExchange(ref _lastWarnTs, now, last) != last) return;
        logger.LogWarning(
            "检测到 X-Forwarded-For，但未启用 Scada:ReverseProxy：限流与 IP 白名单按代理地址计算。" +
            "如确实部署在反向代理后，请配置 Scada:ReverseProxy:Enabled 与 KnownProxies");
    }
}
