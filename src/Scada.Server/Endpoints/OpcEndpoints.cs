using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;
using Opc.Ua;

namespace Scada.Server.Endpoints;

public static class OpcEndpoints
{
    public static void MapOpcEndpoints(this IEndpointRouteBuilder app)
    {
        var devices = app.MapGroup("/api/devices/{id:int}/opc").RequireAuthorization("Admin");
        devices.MapPost("/browse", (int id, OpcBrowseRequest request, HttpContext http, OpcCatalogService service, CancellationToken ct) =>
            Run(async () => Results.Ok(await service.BrowseAsync(http.User.GetUserId()!.Value, id, request, ct))));
        devices.MapDelete("/browse/{cursor}", (int id, string cursor, HttpContext http, OpcCatalogService service) =>
            Run(async () => { await service.CloseAsync(http.User.GetUserId()!.Value, id, cursor); return Results.Ok(); }));
        devices.MapPost("/import/preview", (int id, OpcImportRequest request, HttpContext http, OpcCatalogService service, CancellationToken ct) =>
            Run(async () => Results.Ok(await service.PreviewAsync(http.User.GetUserId()!.Value, id, request, ct))));
        devices.MapPost("/import/{previewId}", (int id, string previewId, HttpContext http, OpcCatalogService service, ConfigReloader reloader, CancellationToken ct) =>
            Run(async () => { var tags = await service.ApplyAsync(http.User.GetUserId()!.Value, id, previewId, http.User.GetUserName(), ct); await reloader.ReloadAsync(ct); return Results.Ok(tags); }));
        var group = app.MapGroup("/api/opc").RequireAuthorization("Admin");
        group.MapGet("/certificates", (OpcCertificateService service, CancellationToken ct) => Run(async () => Results.Ok(await service.ListAsync(ct))));
        group.MapGet("/certificates/{sha256}/public", (string sha256, OpcCertificateService service, CancellationToken ct) =>
            Run(async () => Results.File(await service.PublicCertificateAsync(sha256, ct), "application/pkix-cert", "scada-client.der")));
        group.MapPost("/certificates/{sha256}/trust", (string sha256, HttpContext http, OpcCertificateService service, ICollectorControl collector,
            OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => ChangeTrust(sha256, true, http, service, collector, catalog, factory, ct));
        group.MapDelete("/certificates/{sha256}/trust", (string sha256, HttpContext http, OpcCertificateService service, ICollectorControl collector,
            OpcCatalogService catalog, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) => ChangeTrust(sha256, false, http, service, collector, catalog, factory, ct));
    }
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
        catch (ArgumentException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
        catch (OperationCanceledException) { return Results.Problem("OPC UA 操作取消或超时", statusCode: 504); }
        catch (Exception ex) when (ex is IOException or ServiceResultException or TimeoutException) { return Results.Problem(ex.Message, statusCode: 502); }
    }
}
