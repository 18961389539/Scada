using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public static class CollectorDiagnosticEndpoints
{
    public static void MapCollectorDiagnosticEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices/{id:int}/diagnostics").RequireAuthorization("Admin");
        group.MapGet("", (int id, ICollectorDiagnostics collector, IConfigSnapshot config, HttpContext http, GatewayCenterService gateways, int? page, int? pageSize,
            string? search, TagQuality? quality, bool? staleOnly, CancellationToken ct) => GatewayEndpoints.Run(http, async () =>
        {
            var query = new CollectorDiagnosticQuery(page ?? 1, pageSize ?? 100, search, quality, staleOnly ?? false);
            try { CollectorDiagnosticPaging.Validate(query); } catch (ArgumentException ex) { return EndpointHelpers.BadRequest(ex.Message); }
            if (config.TryGetDevice(id, out var device) && device.GatewayId is int gatewayId)
            {
                var result = await gateways.EngineerAsync<CollectorDiagnosticPage>(id, GatewayEngineeringOperation.Diagnostics, query, http.User.GetUserId()!.Value, ct);
                Validate(result.Diagnostics, id, gatewayId); return Results.Ok(result);
            }
            var snapshot = collector.GetDiagnostics(id); return snapshot is null ? Results.NotFound() : Results.Ok(CollectorDiagnosticPaging.Page(snapshot, query));
        }));
        group.MapGet("/export", (int id, ICollectorDiagnostics collector, IConfigSnapshot config, IOptions<JsonOptions> options, HttpContext http, GatewayCenterService gateways, CancellationToken ct) => GatewayEndpoints.Run(http, async () =>
        {
            CollectorDeviceDiagnostics? snapshot;
            if (config.TryGetDevice(id, out var device) && device.GatewayId is int gatewayId)
            { snapshot = await gateways.EngineerAsync<CollectorDeviceDiagnostics>(id, GatewayEngineeringOperation.DiagnosticsExport, new { }, http.User.GetUserId()!.Value, ct); Validate(snapshot, id, gatewayId); }
            else snapshot = collector.GetDiagnostics(id);
            return snapshot is null ? Results.NotFound() : Results.File(JsonSerializer.SerializeToUtf8Bytes(snapshot, options.Value.SerializerOptions), "application/json", $"collector-{id}-{snapshot.CapturedTs}.json");
        }));
        group.MapPost("/reconnect", (int id, ICollectorDiagnostics collector, IConfigSnapshot config, IDbContextFactory<ConfigDbContext> factory, HttpContext http, GatewayCenterService gateways, CancellationToken ct) => GatewayEndpoints.Run(http, async () =>
        {
            if (config.TryGetDevice(id, out var device) && device.GatewayId is not null)
            {
                var result = await gateways.EngineerAsync<JsonElement>(id, GatewayEngineeringOperation.Reconnect, new { }, http.User.GetUserId()!.Value, ct);
                return Results.Accepted(value: result);
            }
            var snapshot = collector.GetDiagnostics(id); if (snapshot is null) return Results.NotFound();
            if (!snapshot.Enabled || snapshot.PointCount == 0) return Results.Conflict(new { message = "设备停用或没有测点，无可重连的采集任务" });
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(new OperationLog { UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(), Action = $"请求采集重连：设备 #{id} {snapshot.Name}", Result = "管理员请求" });
            await db.SaveChangesAsync(ct);
            return collector.RequestReconnect(id) ? Results.Accepted(value: new { message = "已请求重连，将在当前采集及排队命令结束后执行" }) : Results.Conflict(new { message = "设备配置已变化，请刷新后重试" });
        }));
    }
    private static void Validate(CollectorDeviceDiagnostics? snapshot, int deviceId, int gatewayId)
    {
        if (snapshot is null || snapshot.DeviceId != deviceId || snapshot.GatewayId != gatewayId || !GatewayProtocol.IsRevision(snapshot.ConfigurationRevision)
            || snapshot.Points is null || snapshot.ReadGroups is null || snapshot.Events is null || snapshot.Samples is null || snapshot.Quality is null || snapshot.Metrics is null)
            throw new GatewayRequestException(502, "网关诊断响应的设备、采集位置或结构不匹配");
    }
}
