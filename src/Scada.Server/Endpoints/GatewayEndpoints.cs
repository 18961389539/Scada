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
public sealed record GatewayAssignmentRequest(int? GatewayId, int? ExpectedGatewayId = null, long? ExpectedDisabledSinceTs = null);

public static class GatewayEndpoints
{
    public static void MapGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/gateways").RequireAuthorization(AccessPolicies.GlobalConfigure);
        admin.MapGet("", (GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () => Results.Ok(await service.ListAsync(ct))));
        admin.MapGet("/{id:int}", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { var gateway = (await service.ListAsync(ct)).SingleOrDefault(x => x.Id == id); return gateway is null ? Results.NotFound() : Results.Ok(gateway); }));
        admin.MapGet("/{id:int}/devices", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () => Results.Ok(await service.ManagedDevicesAsync(id, ct))));
        admin.MapGet("/{id:int}/dead-letters", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () => Results.Ok(await service.ListDeadLettersAsync(id, ct))));
        admin.MapGet("/{id:int}/dead-letters/{streamId:guid}/{sequence:long}", (int id, Guid streamId, long sequence, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { var letter = await service.GetDeadLetterAsync(id, streamId, sequence, ct); return letter is null ? Results.NotFound() : Results.Ok(letter); }));
        admin.MapDelete("/{id:int}/dead-letters/{streamId:guid}/{sequence:long}", (int id, Guid streamId, long sequence, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            await service.DiscardDeadLetterAsync(id, streamId, sequence, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            return Results.NoContent();
        }));
        admin.MapGet("/assignments/{deviceId:int}", (int deviceId, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () => Results.Ok(await service.AssignmentInfoAsync(deviceId, ct))));
        admin.MapPost("", (GatewayCreateRequest request, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            var created = await service.CreateAsync(request.Name, http.User.GetUserId()!.Value, http.User.GetUserName(), ct);
            return Results.Created($"/api/gateways/{created.Id}", new { created.Id, created.Token });
        }));
        admin.MapPut("/{id:int}", (int id, GatewayUpdateRequest request, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { await service.ChangeAsync(id, request.Name, request.Enabled, false, false, http.User.GetUserId()!.Value, http.User.GetUserName(), ct, ExpectedRevision(http)); return Results.Ok(); }));
        admin.MapPost("/{id:int}/rotate-key", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { var token = await service.ChangeAsync(id, null, null, true, false, http.User.GetUserId()!.Value, http.User.GetUserName(), ct, ExpectedRevision(http)); return Results.Ok(new { token }); }));
        admin.MapPost("/{id:int}/release-session", (int id, GatewayCenterService service, HttpContext http, CancellationToken ct) => Run(http, async () =>
        { await service.ChangeAsync(id, null, null, false, true, http.User.GetUserId()!.Value, http.User.GetUserName(), ct, ExpectedRevision(http)); return Results.Ok(); }));
        admin.MapPost("/assignments/{deviceId:int}", (int deviceId, GatewayAssignmentRequest request, GatewayCenterService service, ConfigReloader reloader, HttpContext http, CancellationToken ct) => Run(http, async () =>
        {
            if (request.ExpectedDisabledSinceTs is < 0) throw new GatewayRequestException(400, "设备停用时间无效");
            var condition = request.ExpectedDisabledSinceTs is long ts ? new GatewayAssignmentCondition(request.ExpectedGatewayId, ts) : null;
            await service.AssignAsync(deviceId, request.GatewayId, http.User.GetUserId()!.Value, http.User.GetUserName(), ct, condition); await reloader.ReloadAsync(ct); return Results.Ok();
        }));

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
        agent.MapGet("/commands", (GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.CommandOffersAsync(Caller(http), Session(http), ct))));
        agent.MapPost("/commands/{id:guid}/authorize", (Guid id, GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.AuthorizeCommandAsync(Caller(http), Session(http), id, ct))));
        agent.MapPost("/command-results", (GatewayCommandReceipt receipt, GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.ReceiveCommandResultAsync(Caller(http), Session(http), receipt, ct))));
        agent.MapGet("/engineering", (Guid? active, GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.EngineeringOfferAsync(Caller(http), Session(http), active, ct))));
        agent.MapPost("/engineering-results", (GatewayEngineeringReply reply, GatewayCenterService service, HttpContext http, CancellationToken ct) =>
            Run(http, async () => Results.Ok(await service.EngineeringReplyAsync(Caller(http), Session(http), reply, ct))));
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
    private static string? ExpectedRevision(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue("If-Match", out var header)) return null;
        var value = header.ToString().Trim('"');
        return GatewayProtocol.IsRevision(value) ? value : throw new GatewayRequestException(400, "网关管理版本标识无效");
    }
    private static Guid Session(HttpContext http) => Guid.TryParse(http.Request.Headers["X-Scada-Session"], out var value) && value != Guid.Empty ? value : throw new GatewayRequestException(409, "缺少有效的网关会话，请先握手", "session_expired");
    internal static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return await action(); }
        catch (GatewayRequestException ex) { return Results.Json(new { message = ex.Message, code = ex.Code }, statusCode: ex.StatusCode); }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            EndpointHelpers.LogFailure(http, "Scada.Server.Endpoints.GatewayEndpoints", ex, LogLevel.Warning);
            return EndpointHelpers.BadRequest("网关 JSON 或字符编码无效");
        }
        catch (Exception ex) when (ex is DbUpdateException or Microsoft.Data.Sqlite.SqliteException or CryptographicException)
        {
            EndpointHelpers.LogFailure(http, "Scada.Server.Endpoints.GatewayEndpoints", ex);
            return Results.Problem("中心暂时无法持久保存或读取受保护配置，请保留网关数据并重试", statusCode: 503);
        }
    }
}
