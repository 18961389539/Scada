using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;
using Opc.Ua;
using Scada.Collector.Opc;
using System.Text.Json;

namespace Scada.Server.Endpoints;

public static class OpcEndpoints
{
    public static void MapOpcEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices/{id:int}/opc").RequireAuthorization("Admin");
        devices.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        devices.MapPost("/browse", (int id, OpcBrowseRequest request, HttpContext http, OpcCatalogService service, CancellationToken ct) =>
            Run(async () => Results.Ok(await service.BrowseAsync(http.User.GetUserId()!.Value, id, request, ct))));
        devices.MapDelete("/browse/{cursor}", (int id, string cursor, HttpContext http, OpcCatalogService service, CancellationToken ct) =>
            Run(async () => { await service.CloseAsync(http.User.GetUserId()!.Value, id, cursor, ct); return Results.Ok(); }));
        devices.MapPost("/import/preview", (int id, OpcImportRequest request, HttpContext http, OpcCatalogService service, CancellationToken ct) =>
            Run(async () => Results.Ok(await service.PreviewAsync(http.User.GetUserId()!.Value, id, request, ct))));
        devices.MapPost("/import/{previewId}", (int id, string previewId, HttpContext http, OpcCatalogService service, ConfigReloader reloader, CancellationToken ct) =>
            Run(async () => { var tags = await service.ApplyAsync(http.User.GetUserId()!.Value, id, previewId, http.User.GetUserName(), ct); await reloader.ReloadAsync(ct); return Results.Ok(tags); }));
        var group = app.MapGroup("/api/opc").RequireAuthorization("Admin");
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        group.MapGet("/certificates", (OpcCertificateService service, CancellationToken ct) => Run(async () => Results.Ok(await service.ListAsync(ct))));
        group.MapGet("/certificates/{sha256}/public", (string sha256, OpcCertificateService service, CancellationToken ct) =>
            Run(async () => Results.File(await service.PublicCertificateAsync(sha256, ct), "application/pkix-cert", "scada-client.der")));
        group.MapPost("/certificates/{sha256}/trust", (string sha256, HttpContext http, OpcCertificateService service, ICollectorControl collector,
            OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => ChangeTrust(sha256, true, http, service, collector, catalog, factory, ct));
        group.MapDelete("/certificates/{sha256}/trust", (string sha256, HttpContext http, OpcCertificateService service, ICollectorControl collector,
            OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => ChangeTrust(sha256, false, http, service, collector, catalog, factory, ct));
        devices.MapGet("/certificates", (int id, int? gatewayId, HttpContext http, OpcCertificateService local, GatewayCenterService gateways, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => Run(async () =>
        {
            var device = await CertificateDeviceAsync(factory, id, gatewayId, ct); Scope(http, device);
            return Results.Ok(device.GatewayId is null ? await local.ListAsync(ct) : await gateways.EngineerAsync<IReadOnlyList<OpcCertificateInfo>>(id, GatewayEngineeringOperation.OpcCertificates, new { }, http.User.GetUserId()!.Value, ct, device.GatewayId));
        }));
        devices.MapGet("/certificates/{sha256}/public", (int id, int? gatewayId, string sha256, HttpContext http, OpcCertificateService local, GatewayCenterService gateways, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => Run(async () =>
        {
            var device = await CertificateDeviceAsync(factory, id, gatewayId, ct); Scope(http, device);
            var bytes = device.GatewayId is null ? await local.PublicCertificateAsync(sha256, ct) : await gateways.EngineerAsync<byte[]>(id, GatewayEngineeringOperation.OpcPublicCertificate, new { sha256 }, http.User.GetUserId()!.Value, ct, device.GatewayId);
            return Results.File(bytes, "application/pkix-cert", "scada-client.der");
        }));
        devices.MapPost("/certificates/{sha256}/trust", (int id, int? gatewayId, string sha256, HttpContext http, OpcCertificateService local, GatewayCenterService gateways,
            ICollectorControl collector, OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => ScopedTrust(id, gatewayId, sha256, true, http, local, gateways, collector, catalog, factory, ct));
        devices.MapDelete("/certificates/{sha256}/trust", (int id, int? gatewayId, string sha256, HttpContext http, OpcCertificateService local, GatewayCenterService gateways,
            ICollectorControl collector, OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => ScopedTrust(id, gatewayId, sha256, false, http, local, gateways, collector, catalog, factory, ct));
    }
    private static Task<IResult> ScopedTrust(int id, int? expectedGateway, string sha256, bool trust, HttpContext http, OpcCertificateService local, GatewayCenterService gateways,
        ICollectorControl collector, OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => Run(async () =>
    {
        var device = await CertificateDeviceAsync(factory, id, expectedGateway, ct); Scope(http, device);
        if (device.GatewayId is null) return await ChangeTrust(sha256, trust, http, local, collector, catalog, factory, ct);
        await gateways.EngineerAsync<JsonElement>(id, trust ? GatewayEngineeringOperation.OpcTrust : GatewayEngineeringOperation.OpcUntrust, new { sha256 }, http.User.GetUserId()!.Value, ct, device.GatewayId);
        await catalog.InvalidateAsync(device.GatewayId); return Results.Ok();
    });
    private static async Task<Device> CertificateDeviceAsync(IDbContextFactory<ConfigDbContext> factory, int id, int? expectedGateway, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("设备不存在");
        if (device.Protocol != Scada.Core.Enums.ProtocolType.OpcUa) throw new ArgumentException("此设备不是 OPC UA 协议");
        if (expectedGateway is not null && device.GatewayId != expectedGateway) throw new InvalidOperationException("设备采集位置已变化，请刷新并重新核对证书作用范围");
        return device;
    }
    private static void Scope(HttpContext http, Device device) => http.Response.Headers["X-Scada-Acquisition-Scope"] = device.GatewayId is int gateway ? $"gateway:{gateway}" : "centre";
    private static Task<IResult> ChangeTrust(string sha256, bool trust, HttpContext http, OpcCertificateService service, ICollectorControl collector,
        OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => Run(async () =>
    {
        await service.SetTrustAsync(sha256, trust, ct); await catalog.InvalidateAsync(); collector.RequestReload();
        await using var db = await factory.CreateDbContextAsync(ct);
        db.OperationLogs.Add(new OperationLog { UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(),
            Action = trust ? "信任 OPC UA 服务器证书" : "撤销 OPC UA 服务器证书信任", NewValue = sha256, Result = "成功" });
        await db.SaveChangesAsync(ct); return Results.Ok();
    });
    private static async Task<IResult> Run(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (GatewayRequestException ex) { return Results.Json(new { message = ex.Message }, statusCode: ex.StatusCode); }
        catch (ArgumentException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
        catch (OperationCanceledException) { return Results.Problem("OPC UA 操作取消或超时", statusCode: 504); }
        catch (Exception ex) when (ex is IOException or ServiceResultException or TimeoutException) { return Results.Problem(ex.Message, statusCode: 502); }
    }
}
