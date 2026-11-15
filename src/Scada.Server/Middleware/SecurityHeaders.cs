using Microsoft.Extensions.Options;
using Scada.Core.Options;

namespace Scada.Server.Middleware;

/// <summary>
/// 统一注入安全响应头。放在管道最前（但在请求日志之后），让静态资源、API 与错误响应拿到同一套头。
///
/// 这里的 CSP 是给本仓库前端（Vite + Vue 3 + Element Plus + SignalR）量身定制的：
/// <list type="bullet">
///   <item><c>script-src 'self'</c>：产物是外部 ES module，没有 inline script，所以不放 <c>unsafe-inline</c>；
///   一旦以后有人在 index.html 里内联脚本，这里会立刻拦下——这正是想要的效果。</item>
///   <item><c>style-src 'unsafe-inline'</c>：Vue 组件与 Element Plus 大量使用 inline style，去掉会样式全失。</item>
///   <item><c>connect-src 'self' ws: wss:</c>：SignalR 走 WebSocket，同源但协议不同，必须显式列出。</item>
///   <item><c>img-src/blob: worker-src blob:</c>：画面导出、离线包与前端生成的对象 URL。</item>
/// </list>
///
/// HSTS 只在请求已是 HTTPS 时下发：现场大量部署是局域网明文 HTTP，对 HTTP 响应发 HSTS 无效且会误导排查。
/// </summary>
public sealed class SecurityHeaders(RequestDelegate next, IOptions<ScadaSettings> options, ILogger<SecurityHeaders> logger)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss:; " +
        "worker-src 'self' blob:; " +
        "child-src 'self' blob:; " +
        "frame-src 'self' blob:; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'self'";

    private readonly bool _enabled = options.Value.SecurityHeaders;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_enabled)
        {
            await next(context);
            return;
        }

        var headers = context.Response.Headers;
        // 用索引器而不是 Append：中间件可能被重复注册，重复键会让浏览器按最严格的一条解析，纯属噪音。
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "SAMEORIGIN";
        headers["Referrer-Policy"] = "same-origin";
        // 禁用不需要的浏览器能力：现场终端不该弹摄像头/定位授权。
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        if (context.Request.IsHttps)
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";

        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            // 头已经写进响应，但异常处理中间件在更外层，会重写状态码而不重写头——这里只补一条日志便于定位。
            logger.LogDebug(ex, "响应头写入后发生异常：{Method} {Path}", context.Request.Method, context.Request.Path);
            throw;
        }
    }
}
