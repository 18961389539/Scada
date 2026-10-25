using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record HmiLayoutRequest(string Name, HmiLayoutDocument Document, int Revision = 0);
public sealed record HmiWorkstationRequest(string Name, string? Description, int? LayoutId, bool AllowControl = true);

// 运行布局（多显示器/区域）与工作站分配。
public static class HmiLayoutEndpoints
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapHmiLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        var layouts = app.MapGroup("/api/hmi/layouts").RequireAuthorization();
        layouts.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok((await db.HmiLayouts.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)).Select(View));
        });
        layouts.MapPost("", (HmiLayoutRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveAsync(null, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapPut("/{id:int}", (int id, HmiLayoutRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveAsync(id, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Results.NotFound();
            db.HmiLayouts.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除运行布局 {row.Name}", "HmiLayout", row.Id, View(row), null));
            await db.SaveChangesAsync(ct); // 分配了该布局的工作站置空（外键 SetNull）
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        var ws = app.MapGroup("/api/hmi/workstations").RequireAuthorization();
        ws.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(await db.HmiWorkstations.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));
        });
        // 本机工作站解析：返回工作站与其布局（任何登录用户可读，用于自动打开布局）。
        ws.MapGet("/resolve", async (string name, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var station = await db.HmiWorkstations.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, ct);
            if (station is null) return Results.NotFound(new { message = "工作站未登记" });
            var layout = station.LayoutId is int lid ? await db.HmiLayouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lid, ct) : null;
            return Results.Ok(new { workstation = station, layout = layout is null ? null : View(layout) });
        });
        ws.MapPost("", (HmiWorkstationRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveStationAsync(null, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        ws.MapPut("/{id:int}", (int id, HmiWorkstationRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveStationAsync(id, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        ws.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.HmiWorkstations.SingleOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Results.NotFound();
            db.HmiWorkstations.Remove(row); db.OperationLogs.Add(Audit.Change(http, $"删除工作站 {row.Name}", "HmiWorkstation", row.Id, row, null));
            await db.SaveChangesAsync(ct); return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
    }

    private static async Task<IResult> SaveAsync(int? id, HmiLayoutRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return EndpointHelpers.BadRequest("名称须为 1～100 个字符");
        await using var db = await factory.CreateDbContextAsync(ct);
        var published = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        if (HmiLayoutValidator.Validate(r.Document, published) is string error) return EndpointHelpers.BadRequest(error);
        var row = id is int rid ? await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == rid, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (await db.HmiLayouts.AnyAsync(x => x.Name == name && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("名称已存在");
        if (row is not null && row.Revision != r.Revision) return Results.Conflict(new { message = "布局已被他人修改，请刷新" });
        var before = row is null ? null : View(row);
        row ??= db.HmiLayouts.Add(new HmiLayout()).Entity;
        if (before is not null) row.Revision++;
        row.Name = name; row.DocumentJson = JsonSerializer.Serialize(r.Document, Json); row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
        await db.SaveChangesAsync(ct);
        db.OperationLogs.Add(Audit.Change(http, $"{(before is null ? "新建" : "修改")}运行布局 {name}", "HmiLayout", row.Id, before, View(row)));
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(row));
    }

    private static async Task<IResult> SaveStationAsync(int? id, HmiWorkstationRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var name = r.Name?.Trim() ?? "";
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]{1,60}$")) return EndpointHelpers.BadRequest("工作站名称须为 1～60 个字母、数字、点、下划线或连字符");
        if ((r.Description ?? "").Length > 200) return EndpointHelpers.BadRequest("说明最多 200 个字符");
        await using var db = await factory.CreateDbContextAsync(ct);
        if (r.LayoutId is int lid && !await db.HmiLayouts.AnyAsync(x => x.Id == lid, ct)) return EndpointHelpers.BadRequest("布局不存在");
        var row = id is int sid ? await db.HmiWorkstations.SingleOrDefaultAsync(x => x.Id == sid, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (await db.HmiWorkstations.AnyAsync(x => x.Name == name && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("名称已存在");
        var before = row is null ? null : new { row.Name, row.LayoutId, row.AllowControl, row.Description };
        row ??= db.HmiWorkstations.Add(new HmiWorkstation()).Entity;
        row.Name = name; row.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(); row.LayoutId = r.LayoutId; row.AllowControl = r.AllowControl;
        row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
        await db.SaveChangesAsync(ct);
        db.OperationLogs.Add(Audit.Change(http, $"{(before is null ? "登记" : "修改")}工作站 {name}", "HmiWorkstation", row.Id, before, new { row.Name, row.LayoutId, row.AllowControl, row.Description }));
        await db.SaveChangesAsync(ct);
        return Results.Ok(row);
    }

    public static object View(HmiLayout x) => new { x.Id, x.Name, x.Revision, x.UpdatedTs, x.UpdatedBy, document = JsonSerializer.Deserialize<HmiLayoutDocument>(x.DocumentJson, Json) };
}
