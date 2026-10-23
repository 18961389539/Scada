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

namespace Scada.Server.Endpoints;

public static class CollectorDiagnosticEndpoints
{
    public static void MapCollectorDiagnosticEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices/{id:int}/diagnostics").RequireAuthorization("Admin");
        group.MapGet("", (int id, ICollectorDiagnostics collector, HttpContext http, int? page, int? pageSize,
            string? search, TagQuality? quality, bool? staleOnly) =>
        {
            if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 200 || search?.Length > 200 || quality is not null && !Enum.IsDefined(quality.Value))
                return EndpointHelpers.BadRequest("分页、搜索或质量参数无效（每页 1～200 条）");
            var snapshot = collector.GetDiagnostics(id);
            if (snapshot is null) return Results.NotFound();
            var filtered = snapshot.Points.Where(x => (quality is null || x.Quality == quality)
                && (staleOnly != true || x.Stale)
                && (string.IsNullOrWhiteSpace(search) || x.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || x.Address.Contains(search, StringComparison.OrdinalIgnoreCase) || x.TagId.ToString() == search)).ToList();
            var index = page ?? 1; var size = pageSize ?? 100;
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { diagnostics = snapshot with { Points = filtered.Skip((index - 1) * size).Take(size).ToArray() }, total = filtered.Count, page = index, pageSize = size });
        });
        group.MapGet("/export", (int id, ICollectorDiagnostics collector, IOptions<JsonOptions> options, HttpContext http) =>
        {
            var snapshot = collector.GetDiagnostics(id);
            if (snapshot is null) return Results.NotFound();
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(snapshot, options.Value.SerializerOptions), "application/json", $"collector-{id}-{snapshot.CapturedTs}.json");
        });
        group.MapPost("/reconnect", async (int id, ICollectorDiagnostics collector, IDbContextFactory<ConfigDbContext> factory, HttpContext http, CancellationToken ct) =>
        {
            var snapshot = collector.GetDiagnostics(id);
            if (snapshot is null) return Results.NotFound();
            if (!snapshot.Enabled || snapshot.PointCount == 0) return Results.Conflict(new { message = "设备停用或没有测点，无可重连的采集任务" });
            await using var db = await factory.CreateDbContextAsync(ct);
            db.OperationLogs.Add(new OperationLog { UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(),
                Action = $"请求采集重连：设备 #{id} {snapshot.Name}", Result = "管理员请求" });
            await db.SaveChangesAsync(ct);
            return collector.RequestReconnect(id)
                ? Results.Accepted(value: new { message = "已请求重连，将在当前采集及排队命令结束后执行" })
                : Results.Conflict(new { message = "设备配置已变化，请刷新后重试" });
        });
    }
}
