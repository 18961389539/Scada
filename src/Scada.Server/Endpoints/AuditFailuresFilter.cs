using Microsoft.EntityFrameworkCore;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

// 写操作返回 400/404/409/422 时补一条失败审计；403 已由授权层记录，2xx 由端点自己写差分。
public sealed class AuditFailuresFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)) return result;
        var status = result is IStatusCodeHttpResult s ? s.StatusCode : null;
        if (status is 400 or 404 or 409 or 422)
        {
            var message = result is IValueHttpResult { Value: { } value } ? value.GetType().GetProperty("message")?.GetValue(value)?.ToString() : null;
            try
            {
                var factory = http.RequestServices.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
                await Audit.FailedAsync(factory, http, $"{http.Request.Method} {http.Request.Path}", $"HTTP {status}{(message is null ? "" : "：" + message)}", CancellationToken.None);
            }
            catch { }
        }

        return result;
    }
}
