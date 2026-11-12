using Scada.Core.Security;
using Scada.Server.Mcp;

namespace Scada.Server.Auth;

/// <summary>
/// 把请求身份放进 <see cref="AuditContext"/>：拦截器和手工审计读同一个来源，
/// 不会出现「拦截器记 mcp:lan、手工记空用户名」这种同一操作两种身份的情况。
/// </summary>
public sealed class AuditActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        using var _ = AuditContext.Begin(Resolve(http));
        await next(http);
    }

    private static AuditActor Resolve(HttpContext http)
    {
        // MCP 端点没有登录态（靠来源网段与写开关限制），身份固定 mcp:lan。
        if (http.Request.Path.StartsWithSegments(McpEndpoints.Path))
            return new AuditActor(null, McpActors.McpActorName, "mcp");
        if (http.User.Identity?.IsAuthenticated == true)
            return new AuditActor(http.User.GetUserId(), http.User.GetUserName(), "http");
        return AuditActor.Anonymous;
    }
}
