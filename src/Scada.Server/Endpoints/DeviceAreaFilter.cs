using Scada.Core.Security;

namespace Scada.Server.Endpoints;

// 路由中带设备 id 的工程接口（诊断、重连、OPC 浏览/导入/证书）按设备所在区域校验。
public sealed class DeviceAreaFilter(Permission permission, string routeKey = "id") : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.Request.RouteValues.TryGetValue(routeKey, out var raw) && int.TryParse(raw?.ToString(), out var deviceId))
        {
            if (await EndpointHelpers.RequireDeviceAsync(http, deviceId, permission, $"{http.Request.Method} {http.Request.Path}") is { } denied)
                return denied;
        }

        return await next(context);
    }
}
