using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record GatewayCreateRequest(string Name);
public sealed record GatewayUpdateRequest(string Name, bool Enabled);
public sealed record GatewayAssignmentRequest(int? GatewayId);

public static class GatewayEndpoints
{
    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/gateways").RequireAuthorization("Admin");
        admin.MapGet("", (GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () => Results.Ok(await service.ListAsync(ct))));
        admin.MapGet("/{id:int}", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { var gateway = (await service.ListAsync(ct)).SingleOrDefault(x => x.Id == id); return gateway is null ? Results.NotFound() : Results.Ok(gateway); }));
        admin.MapPost("", (GatewayCreateRequest request, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var created = await service.CreateAsync(request.Name, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            return Results.Created($"/api/gateways/{created.Id}", new { created.Id, created.Token });
        }));
        admin.MapPut("/{id:int}", (int id, GatewayUpdateRequest request, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { await service.ChangeAsync(id, request.Name, request.Enabled, false, false, http.User.GetUserId()!.Value, http.User.GetUserName(), ct); return Results.Ok(); }));
        admin.MapPost("/{id:int}/rotate-key", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { var token = await service.ChangeAsync(id, null, null, true, false, http.User.GetUserId()!.Value, http.User.GetUserName(), ct); return Results.Ok(new { token }); }));
        admin.MapPost("/{id:int}/release-session", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { await service.ChangeAsync(id, null, null, false, true, http.User.GetUserId()!.Value, http.User.GetUserName(), ct); return Results.Ok(); }));
        admin.MapPost("/assignments/{deviceId:int}", (int deviceId, GatewayAssignmentRequest request, GatewayCenterService service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { await service.AssignAsync(deviceId, request.GatewayId, http.User.GetUserId()!.Value, http.User.GetUserName(), ct); await reloader.ReloadAsync(ct); return Results.Ok(); }));

        var agent = app.MapGroup("/api/gateway-agent").RequireAuthorization("GatewayAgent");
        agent.MapPost("/hello", (GatewayHello request, GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.HelloAsync(Caller(http), request, ct))));
        agent.MapGet("/configuration", (GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () =>
            {
                var known = http.Request.Headers.IfNoneMatch.ToString().Trim('"');
                var configuration = await service.ConfigurationUpdateAsync(Caller(http), Session(http), GatewayProtocol.IsRevision(known) ? known : null, ct);
                if (configuration is null) return Results.StatusCode(304);
                http.Response.Headers.ETag = $"\"{configuration.Revision}\""; return Results.Ok(configuration);
            }));
        agent.MapPost("/heartbeat", (GatewayHeartbeat request, GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.HeartbeatAsync(Caller(http), Session(http), request, ct))));
        agent.MapPost("/goodbye", (GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.GoodbyeAsync(Caller(http), Session(http), ct))));
        agent.MapPost("/telemetry", (GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var session = Session(http);
            if (http.Request.ContentLength > GatewaySettings.MaxPacketBytes) throw new GatewayRequestException(413, "网关数据包过大");
            await using var memory = new MemoryStream(); var buffer = new byte[64 * 1024]; int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
            { if (memory.Length + count > GatewaySettings.MaxPacketBytes) throw new GatewayRequestException(413, "网关数据包过大"); await memory.WriteAsync(buffer.AsMemory(0, count), ct); }
            var payload = new UTF8Encoding(false, true).GetString(memory.GetBuffer(), 0, (int)memory.Length);
            return Results.Ok(await service.AcceptAsync(Caller(http), session, payload, http.Request.Headers["X-Scada-Payload-Sha256"].ToString(), ct));
        }));
    }
    private static GatewayCaller Caller(HttpContext http) => new(int.Parse(http.User.FindFirstValue("gateway-id")!), http.User.FindFirstValue("gateway-key-stamp")!);
    private static Guid Session(HttpContext http) => Guid.TryParse(http.Request.Headers["X-Scada-Session"], out var value) && value != Guid.Empty ? value : throw new GatewayRequestException(409, "缺少有效的网关会话，请先握手", "session_expired");
    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (GatewayRequestException ex) { return Results.Json(new { message = ex.Message, code = ex.Code }, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { return EndpointHelpers.BadRequest("网关 JSON 或字符编码无效"); }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException or CryptographicException)
        { return Results.Problem("中心暂时无法持久保存或读取受保护配置，请保留网关数据并重试", statusCode: 503); }
    }
}
