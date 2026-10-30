using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record HmiLayoutRequest(string Name, HmiLayoutDocument Document, int Revision = 0);
public sealed record HmiLayoutPublishRequest(int Revision, string Note);
public sealed record HmiLayoutRollbackRequest(int Revision, int TargetRevision, string Note);
public sealed record HmiWorkstationRequest(string Name, string? Description, int? LayoutId, bool AllowControl = true, long? ExpectedUpdatedTs = null);

// 运行布局（多显示器/区域）与工作站分配。
public static class HmiLayoutEndpoints
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapHmiLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        var layouts = app.MapGroup("/api/hmi/layouts").RequireAuthorization();
        layouts.MapGet("", async (bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (draft == true && !(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return Results.Forbid();
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.HmiLayouts.AsNoTracking();
            if (draft != true) query = query.Where(x => x.PublishedJson != null);
            var rows = await query.ToListAsync(ct);
            return Results.Ok(rows.OrderBy(x => draft == true ? x.Name : x.PublishedName).Select(x => View(x, draft == true)));
        });
        layouts.MapGet("/{id:int}", async (int id, bool? draft, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (draft == true && !(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return Results.Forbid();
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.HmiLayouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null || draft != true && row.PublishedJson is null) return Results.NotFound();
            return Results.Ok(View(row, draft == true));
        });
        layouts.MapPost("", (HmiLayoutRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveAsync(null, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapPut("/{id:int}", (int id, HmiLayoutRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveAsync(id, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapPost("/{id:int}/publish", (int id, HmiLayoutPublishRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => PublishAsync(id, r.Revision, r.Note, null, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapPost("/{id:int}/rollback", (int id, HmiLayoutRollbackRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => PublishAsync(id, r.Revision, r.Note, r.TargetRevision, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapGet("/{id:int}/versions", async (int id, int? limit, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.HmiLayouts.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            return Results.Ok(await db.HmiLayoutVersions.AsNoTracking().Where(x => x.LayoutId == id).OrderByDescending(x => x.Revision)
                .Take(Math.Clamp(limit ?? 100, 1, 200)).Select(x => new { x.Revision, x.Name, x.PublishedTs, x.PublishedBy, x.Note, x.RestoredFromRevision, x.Kind }).ToListAsync(ct));
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapGet("/{id:int}/versions/{revision:int}", async (int id, int revision, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var version = await db.HmiLayoutVersions.AsNoTracking().SingleOrDefaultAsync(x => x.LayoutId == id && x.Revision == revision, ct);
            return version is null ? Results.NotFound() : Results.Ok(new { version.Revision, version.Name, version.PublishedTs, version.PublishedBy, version.Note, version.RestoredFromRevision, version.Kind, Document = JsonSerializer.Deserialize<HmiLayoutDocument>(version.DocumentJson, Json) });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
        layouts.MapDelete("/{id:int}", async (int id, int? revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var row = await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Results.NotFound();
            if (revision != row.Revision) return Conflict();
            // Invalidate workstation edit snapshots when deletion clears their assignment.
            var assigned = await db.HmiWorkstations.Where(x => x.LayoutId == id).ToListAsync(ct);
            foreach (var station in assigned)
            {
                station.LayoutId = null;
                station.UpdatedTs = Math.Max(ScadaTime.NowMs(), station.UpdatedTs + 1);
                station.UpdatedBy = http.User.GetUserName();
            }
            db.HmiLayouts.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除运行布局 {row.Name}", "HmiLayout", row.Id, View(row), null));
            try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Conflict(); }
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);

        var ws = app.MapGroup("/api/hmi/workstations").RequireAuthorization();
        ws.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return Results.Ok(await db.HmiWorkstations.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct));
        });
        // 本机工作站解析：返回工作站与其布局（任何登录用户可读，用于自动打开布局）。
        ws.MapGet("/resolve", async (string? name, int? id, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var station = id is int stableId
                ? await db.HmiWorkstations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == stableId, ct)
                : await db.HmiWorkstations.AsNoTracking().SingleOrDefaultAsync(x => x.Name == name, ct);
            if (station is null) return Results.NotFound(new { message = "工作站未登记" });
            var layout = station.LayoutId is int lid ? await db.HmiLayouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lid && x.PublishedJson != null, ct) : null;
            return Results.Ok(new { workstation = station, layout = layout is null ? null : View(layout, false) });
        });
        ws.MapPost("", (HmiWorkstationRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveStationAsync(null, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        ws.MapPut("/{id:int}", (int id, HmiWorkstationRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveStationAsync(id, r, http, f, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        ws.MapDelete("/{id:int}", async (int id, long? updatedTs, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var row = await db.HmiWorkstations.SingleOrDefaultAsync(x => x.Id == id, ct); if (row is null) return Results.NotFound();
            if (updatedTs != row.UpdatedTs) return StationConflict();
            db.HmiWorkstations.Remove(row); db.OperationLogs.Add(Audit.Change(http, $"删除工作站 {row.Name}", "HmiWorkstation", row.Id, row, null));
            try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
            catch (DbUpdateConcurrencyException) { return StationConflict(); }
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
    }

    private static async Task<IResult> SaveAsync(int? id, HmiLayoutRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return EndpointHelpers.BadRequest("名称须为 1～100 个字符");
        if (HmiLayoutValidator.ValidateDraft(r.Document) is string error) return EndpointHelpers.BadRequest(error);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = id is int rid ? await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == rid, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (await db.HmiLayouts.AnyAsync(x => x.Name == name && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("名称已存在");
        if (row is not null && row.Revision != r.Revision) return Conflict();
        var before = row is null ? null : View(row);
        row ??= db.HmiLayouts.Add(new HmiLayout()).Entity;
        if (before is not null) row.Revision++;
        row.Name = name; row.DocumentJson = JsonSerializer.Serialize(r.Document, Json); row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
        try
        {
            await db.SaveChangesAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"{(before is null ? "新建" : "保存")}运行布局草稿 {name}", "HmiLayout", row.Id, before, View(row)));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok(View(row));
    }

    private static async Task<IResult> PublishAsync(int id, int revision, string? note, int? targetRevision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        note = note?.Trim();
        if (string.IsNullOrEmpty(note) || note.Length > 500) return EndpointHelpers.BadRequest("发布或回退说明须为 1～500 个字符");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.HmiLayouts.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is null) return Results.NotFound();
        if (row.Revision != revision) return Conflict();
        var name = row.Name; var documentJson = row.DocumentJson;
        if (targetRevision is int restore)
        {
            var version = await db.HmiLayoutVersions.AsNoTracking().SingleOrDefaultAsync(x => x.LayoutId == id && x.Revision == restore, ct);
            if (version is null) return Results.NotFound(new { message = "指定的布局发布版本不存在" });
            name = version.Name; documentJson = version.DocumentJson;
        }
        HmiLayoutDocument? document;
        try { document = JsonSerializer.Deserialize<HmiLayoutDocument>(documentJson, Json); }
        catch (JsonException ex) { return EndpointHelpers.BadRequest($"布局文档格式无效：{ex.Message}"); }
        var published = (await db.ProcessScreens.Where(x => x.PublishedJson != null).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        if (HmiLayoutValidator.Validate(document, published) is string error) return EndpointHelpers.BadRequest(error);
        var before = View(row);
        row.Revision++;
        row.PublishedName = name; row.PublishedJson = documentJson; row.PublishedRevision = row.Revision;
        row.PublishedTs = ScadaTime.NowMs(); row.PublishedBy = http.User.GetUserName();
        db.HmiLayoutVersions.Add(new HmiLayoutVersion
        {
            LayoutId = row.Id, Revision = row.Revision, Name = name, DocumentJson = documentJson,
            PublishedTs = row.PublishedTs.Value, PublishedBy = row.PublishedBy, Note = note,
            RestoredFromRevision = targetRevision, Kind = targetRevision is null ? "publish" : "rollback"
        });
        db.OperationLogs.Add(Audit.Change(http, targetRevision is int from ? $"回退运行布局 {name} r{from} → r{row.Revision}：{note}" : $"发布运行布局 {name} r{row.Revision}：{note}", "HmiLayout", row.Id, before, View(row)));
        try { await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        return Results.Ok(View(row));
    }

    private static async Task<IResult> SaveStationAsync(int? id, HmiWorkstationRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct)
    {
        var name = r.Name?.Trim() ?? "";
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_.-]{1,60}$")) return EndpointHelpers.BadRequest("工作站名称须为 1～60 个字母、数字、点、下划线或连字符");
        if ((r.Description ?? "").Length > 200) return EndpointHelpers.BadRequest("说明最多 200 个字符");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = id is int sid ? await db.HmiWorkstations.SingleOrDefaultAsync(x => x.Id == sid, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (row is not null && r.ExpectedUpdatedTs != row.UpdatedTs) return StationConflict();
        if (r.LayoutId is int lid)
        {
            var target = await db.HmiLayouts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lid, ct);
            if (target is null) return EndpointHelpers.BadRequest("布局不存在");
            if (target.PublishedJson is null) return EndpointHelpers.BadRequest("工作站只能绑定已发布的布局，请先发布布局或清除绑定");
        }
        if (await db.HmiWorkstations.AnyAsync(x => x.Name == name && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("名称已存在");
        var before = row is null ? null : new { row.Name, row.LayoutId, row.AllowControl, row.Description };
        row ??= db.HmiWorkstations.Add(new HmiWorkstation()).Entity;
        row.Name = name; row.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(); row.LayoutId = r.LayoutId; row.AllowControl = r.AllowControl;
        row.UpdatedTs = Math.Max(ScadaTime.NowMs(), row.UpdatedTs + 1); row.UpdatedBy = http.User.GetUserName();
        try
        {
            await db.SaveChangesAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"{(before is null ? "登记" : "修改")}工作站 {name}", "HmiWorkstation", row.Id, before, new { row.Name, row.LayoutId, row.AllowControl, row.Description }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { return StationConflict(); }
        return Results.Ok(row);
    }

    private static IResult Conflict() => Results.Conflict(new { message = "布局已被他人修改，请刷新并合并修改" });
    private static IResult StationConflict() => Results.Conflict(new { message = "工作站已被他人修改，请重新加载后核对分配和控制权限" });

    public static object View(HmiLayout x, bool draft = true) => new
    {
        x.Id, Name = draft ? x.Name : x.PublishedName, Revision = draft ? (int?)x.Revision : x.PublishedRevision,
        UpdatedTs = draft ? x.UpdatedTs : x.PublishedTs, UpdatedBy = draft ? x.UpdatedBy : x.PublishedBy,
        Document = JsonSerializer.Deserialize<HmiLayoutDocument>(draft ? x.DocumentJson : x.PublishedJson!, Json),
        x.PublishedRevision, x.PublishedName, x.PublishedTs, x.PublishedBy,
        HasDraftChanges = x.PublishedJson is null || x.Name != x.PublishedName || !JsonNode.DeepEquals(JsonNode.Parse(x.DocumentJson), JsonNode.Parse(x.PublishedJson))
    };
}
