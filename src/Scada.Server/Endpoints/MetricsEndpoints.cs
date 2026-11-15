using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

// Prometheus 文本格式指标。鉴权：Scada:MetricsToken 作为 Bearer 令牌，或具有系统权限的登录令牌。
// 渲染逻辑在 MetricsReport（构造函数注入依赖）；这里只保留鉴权与内容协商。
public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", async (HttpContext http, IOptions<ScadaSettings> settings, Reports.MetricsReport report, IDbContextFactory<RuntimeDbContext> runtime, CancellationToken ct) =>
        {
            if (!await AllowedAsync(http, settings.Value)) return Results.Json(new { message = "需要指标令牌或系统权限" }, statusCode: 401);
            return Results.Text(await report.RenderAsync(settings.Value, runtime, ct), "text/plain; version=0.0.4; charset=utf-8");
        }).AllowAnonymous();
    }

    private static async Task<bool> AllowedAsync(HttpContext http, ScadaSettings settings)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(settings.MetricsToken) && header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var given = Encoding.UTF8.GetBytes(header[7..].Trim());
            if (CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(settings.MetricsToken))) return true;
        }
        if (http.User.Identity?.IsAuthenticated != true || http.User.GetUserId() is null) return false;
        return (await http.GetAccessAsync()).HasGlobal(Permission.System);
    }
}
