using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Kiosk;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Data.Infrastructure;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public static class KioskPlaylistEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapKioskPlaylistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/kiosk-playlists").RequireAuthorization();
        group.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.KioskPlaylists.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(View));
        });
        group.MapGet("/{id:int}", async (int id, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.KioskPlaylists.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            return row is null ? Results.NotFound() : Results.Ok(View(row));
        });
        group.MapPost("/{id:int}/enter-monitor", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, KioskMonitorSessionStore sessions, CancellationToken ct) =>
        {
            if (http.User.GetUserId() is not int userId) return Results.Unauthorized();
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.KioskPlaylists.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.MonitorOnly) sessions.Enter(http, row.Id);
            else sessions.Leave(http);
            return Results.Ok(new { monitorOnly = row.MonitorOnly, entered = row.MonitorOnly });
        });
        group.MapPost("/leave-monitor", (HttpContext http, KioskMonitorSessionStore sessions) =>
        {
            if (http.User.GetUserId() is not int userId) return Results.Unauthorized();
            sessions.Leave(http);
            return Results.NoContent();
        });
        group.MapPost("", (KioskPlaylistRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
            SaveAsync(null, request, http, factory, runtimeFactory, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        group.MapPut("/{id:int}", (int id, KioskPlaylistRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
            SaveAsync(id, request, http, factory, runtimeFactory, ct)).RequireAuthorization(AccessPolicies.GlobalConfigure);
        group.MapDelete("/{id:int}", async (int id, long? revision, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var row = await db.KioskPlaylists.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (revision is long expected && expected != row.UpdatedTs) return Results.Conflict(new { message = "播放列表已被他人修改，请刷新后重试" });
            var before = View(row);
            db.KioskPlaylists.Remove(row);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除 Kiosk 播放列表 {row.Name}", "KioskPlaylist", row.Id, before, null));
            try { await db.SaveChangesAsync(ct); await runtime.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "播放列表已被他人修改，请刷新后重试" }); }
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.GlobalConfigure);
    }

    private static async Task<IResult> SaveAsync(
        int? id, KioskPlaylistRequest request, HttpContext http,
        IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100) return EndpointHelpers.BadRequest("名称须为 1～100 个字符");
        var interval = KioskPlaylistRules.ClampInterval(request.IntervalSeconds);
        if (!KioskPlaylistRules.TryParseScreenIds(JsonSerializer.Serialize(request.ScreenIds ?? []), out var ids, out var parseError))
            return EndpointHelpers.BadRequest(parseError!);

        await using var db = await factory.CreateDbContextAsync(ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var published = (await db.ProcessScreens.AsNoTracking()
            .Where(x => ids.Contains(x.Id) && x.PublishedJson != null)
            .Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        var missing = ids.Where(x => !published.Contains(x)).ToList();
        if (missing.Count > 0) return EndpointHelpers.BadRequest("播放列表只能包含已发布的画面");

        KioskPlaylist row;
        object? before = null;
        if (id is int existingId)
        {
            var existing = await db.KioskPlaylists.SingleOrDefaultAsync(x => x.Id == existingId, ct);
            if (existing is null) return Results.NotFound();
            row = existing;
            if (request.Revision is long expected && expected != row.UpdatedTs)
                return Results.Conflict(new { message = "播放列表已被他人修改，请刷新后重试" });
            before = View(row);
        }
        else
        {
            row = db.KioskPlaylists.Add(new KioskPlaylist()).Entity;
        }

        if (await db.KioskPlaylists.AnyAsync(x => x.Name == name && x.Id != row.Id, ct))
            return EndpointHelpers.BadRequest("已存在同名播放列表");

        row.Name = name;
        row.ScreenIdsJson = KioskPlaylistRules.SerializeScreenIds(ids);
        row.IntervalSeconds = interval;
        row.MonitorOnly = request.MonitorOnly;
        row.UpdatedTs = Math.Max(ScadaTime.NowMs(), row.UpdatedTs + 1);
        row.UpdatedBy = http.User.GetUserName();
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            return EndpointHelpers.BadRequest("已存在同名播放列表");
        }
        var after = View(row);
        runtime.OperationLogs.Add(Audit.Change(http, $"{(id is null ? "新建" : "保存")} Kiosk 播放列表 {row.Name}", "KioskPlaylist", row.Id, before, after));
        await runtime.SaveChangesAsync(ct);
        return id is null ? Results.Created($"/api/kiosk-playlists/{row.Id}", after) : Results.Ok(after);
    }

    internal static object View(KioskPlaylist row)
    {
        KioskPlaylistRules.TryParseScreenIds(row.ScreenIdsJson, out var ids, out _);
        return new
        {
            row.Id,
            row.Name,
            ScreenIds = ids,
            row.IntervalSeconds,
            row.MonitorOnly,
            Revision = row.UpdatedTs,
            row.UpdatedTs,
            row.UpdatedBy
        };
    }
}

public sealed class KioskPlaylistRequest
{
    public string? Name { get; set; }
    public int[]? ScreenIds { get; set; }
    public int IntervalSeconds { get; set; } = KioskPlaylistRules.DefaultIntervalSeconds;
    public bool MonitorOnly { get; set; }
    public long? Revision { get; set; }
}
