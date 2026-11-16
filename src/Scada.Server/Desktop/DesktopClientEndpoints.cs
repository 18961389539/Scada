using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Server.Auth;
using Scada.Server.Endpoints;
using Scada.Server.Ha;

namespace Scada.Server.Desktop;

public sealed record DesktopClientHeartbeatResponse(
    bool Ok, long ServerTs, string Node, string HaRole, DesktopClientPolicyView Policy, int HeartbeatSeconds);

public static class DesktopClientEndpoints
{
    private const int MaxHeartbeatBytes = 4096;

    public static void MapDesktopClientEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/desktop/heartbeat", (
            DesktopClientHeartbeatRequest request,
            HttpContext http,
            DesktopClientRegistry registry,
            IOptionsMonitor<ScadaSettings> settings,
            HaCoordinator ha) =>
        {
            var userId = http.User.GetUserId();
            if (userId is null) return Results.Unauthorized();

            var options = settings.CurrentValue.DesktopClient;
            var node = ha.Enabled ? ha.NodeId : "standalone";
            var role = ha.Status().Role;
            DesktopClientView view;
            try
            {
                view = registry.Heartbeat(new DesktopClientHeartbeat(
                    request.ClientId ?? string.Empty,
                    request.MachineName ?? string.Empty,
                    request.WindowsUser ?? string.Empty,
                    request.AppVersion ?? string.Empty,
                    request.WebView2Version ?? string.Empty,
                    request.BootId,
                    request.TargetKind,
                    request.TargetId,
                    request.TargetName,
                    request.ConfigScope,
                    request.BlockedReason),
                    userId,
                    http.User.GetUserName(),
                    http.Connection.RemoteIpAddress?.ToString(),
                    node,
                    options);
            }
            catch (ArgumentException exception)
            {
                return EndpointHelpers.BadRequest(exception.Message);
            }

            return Results.Ok(new DesktopClientHeartbeatResponse(
                true,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                node,
                role,
                DesktopClientPolicy.Build(view.AppVersion, options),
                HeartbeatSeconds: Math.Clamp(options.HeartbeatSeconds, 10, 600)));
        })
        // 体积上限必须在读请求体之前生效：此前在处理器里看 ContentLength 是模型绑定
        // 之后的事，chunked 请求还没有长度可用，等于没有限制。Kestrel 从端点元数据
        // 取 IRequestSizeLimitMetadata，超限直接 413。
        .WithMetadata(new RequestSizeLimitAttribute(MaxHeartbeatBytes))
        .Produces<DesktopClientHeartbeatResponse>()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .RequireAuthorization()
        .WithName("DesktopClientHeartbeat");

        app.MapGet("/api/desktop/clients", (
            DesktopClientRegistry registry,
            IOptionsMonitor<ScadaSettings> settings,
            HaCoordinator ha) =>
        {
            var options = settings.CurrentValue.DesktopClient;
            var node = ha.Enabled ? ha.NodeId : "standalone";
            var role = ha.Status().Role;
            return Results.Ok(registry.Snapshot(node, role, options));
        })
        .Produces<DesktopClientSnapshot>()
        .Produces(StatusCodes.Status403Forbidden)
        .RequireAuthorization(AccessPolicies.GlobalSystem)
        .WithName("DesktopClientList");
    }
}
