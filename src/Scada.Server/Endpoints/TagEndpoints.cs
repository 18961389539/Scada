using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Services;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record TagRequest(
    int DeviceId,
    string Name,
    string? Description,
    TagArea Area,
    int Address,
    TagDataType DataType,
    double Scale,
    double Offset,
    string? Unit,
    bool Writable,
    ArchiveMode ArchiveMode,
    double Deadband,
    int ArchivePeriodSec,
    double? MinValue,
    double? MaxValue, int DbNumber = 1, int BitOffset = 0, string NodeId = "");

public static class TagEndpoints
{
    public static void MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tags").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (
            int? deviceId,
            string? search,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var query = db.Tags.AsNoTracking();
            if (deviceId is int id)
            {
                query = query.Where(x => x.DeviceId == id);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var keyword = search.Trim();
                query = query.Where(x => x.Name.Contains(keyword) || (x.Description != null && x.Description.Contains(keyword)));
            }

            var access = await http.GetAccessAsync();
            var tags = await query.OrderBy(x => x.DeviceId).ThenBy(x => x.Address).ToListAsync(cancellationToken);
            return Results.Ok(tags.Where(x => access.CanTag(Permission.View, x.Id)).ToList());
        });

        group.MapGet("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return tag is null || !access.CanTag(Permission.View, id) ? Results.NotFound() : Results.Ok(tag);
        });

        group.MapPost("", async (
            TagRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DeviceId, cancellationToken);
            if (device is null)
            {
                return EndpointHelpers.BadRequest("设备不存在");
            }

            if (await EndpointHelpers.RequireAreaAsync(http, device.AreaId, Permission.Configure, $"创建测点：{request.Name}") is { } denied) return denied;

            var tag = MapTag(request);
            var error = ConfigurationValidator.Tag(tag, device);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            if (await db.Tags.AnyAsync(x => x.DeviceId == tag.DeviceId && x.Name == tag.Name, cancellationToken))
            {
                return EndpointHelpers.BadRequest("同一设备下测点名称已存在");
            }

            db.Tags.Add(tag);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                db.OperationLogs.Add(Audit.Change(http, $"创建测点：{tag.Name}", "Tag", tag.Id, null, tag));
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return EndpointHelpers.BadRequest("同一设备下测点名称已存在");
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.Created($"/api/tags/{tag.Id}", tag);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id,
            TagRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var tag = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (tag is null)
            {
                return Results.NotFound();
            }

            if (tag.Kind != TagKind.Physical) return EndpointHelpers.BadRequest("请通过计算测点或计算任务配置修改此测点");
            if (await EndpointHelpers.RequireTagsAsync(http, [id], Permission.Configure, $"修改测点 #{id}") is { } denied) return denied;

            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DeviceId, cancellationToken);
            if (device is null)
            {
                return EndpointHelpers.BadRequest("设备不存在");
            }

            if (await EndpointHelpers.RequireAreaAsync(http, device.AreaId, Permission.Configure, $"修改测点 #{id} 归属设备") is { } deniedTarget) return deniedTarget;
            var before = tag.Clone();

            if (await db.Tags.AnyAsync(
                    x => x.Id != id && x.DeviceId == request.DeviceId && x.Name == request.Name,
                    cancellationToken))
            {
                return EndpointHelpers.BadRequest("同一设备下测点名称已存在");
            }

            var updated = MapTag(request);
            updated.Id = id;
            var error = ConfigurationValidator.Tag(updated, device);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            tag.DeviceId = updated.DeviceId;
            tag.Name = updated.Name;
            tag.Description = updated.Description;
            tag.Area = updated.Area;
            tag.Address = updated.Address;
            tag.DbNumber = updated.DbNumber; tag.BitOffset = updated.BitOffset;
            tag.NodeId = updated.NodeId;
            tag.DataType = updated.DataType;
            tag.Scale = updated.Scale;
            tag.Offset = updated.Offset;
            tag.Unit = updated.Unit;
            tag.Writable = updated.Writable;
            tag.ArchiveMode = updated.ArchiveMode;
            tag.Deadband = updated.Deadband;
            tag.ArchivePeriodSec = updated.ArchivePeriodSec;
            tag.MinValue = updated.MinValue;
            tag.MaxValue = updated.MaxValue;
            db.OperationLogs.Add(Audit.Change(http, $"修改测点：{tag.Name}", "Tag", tag.Id, before, tag));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return EndpointHelpers.BadRequest("同一设备下测点名称已存在");
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(tag);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var tag = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (tag is null)
            {
                return Results.NotFound();
            }

            if (tag.Kind != TagKind.Physical) return EndpointHelpers.BadRequest("请通过计算测点或计算任务配置删除此测点");
            if (await EndpointHelpers.RequireTagsAsync(http, [id], Permission.Configure, $"删除测点 #{id}") is { } denied) return denied;
            if (await db.TagDependencies.AnyAsync(x => x.InputTagId == id, cancellationToken)) return EndpointHelpers.BadRequest("测点仍被计算定义引用，请先修改依赖定义");
            db.Tags.Remove(tag);
            db.OperationLogs.Add(Audit.Change(http, $"删除测点：{tag.Name}", "Tag", tag.Id, tag, null));
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { return Results.Conflict(new { message = "测点引用已变化，请重新读取后修改依赖定义" }); }
            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapGet("/export", async (
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            TagExcelService excel,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            // 只导出有配置权限区域内的设备与测点。
            var devices = (await db.Devices.AsNoTracking().ToListAsync(cancellationToken)).Where(x => access.Has(Permission.Configure, x.AreaId)).ToList();
            var visible = devices.Select(x => x.Id).ToHashSet();
            var tags = (await db.Tags.AsNoTracking().ToListAsync(cancellationToken)).Where(x => x.DeviceId is int d && visible.Contains(d)).ToList();
            var bytes = excel.Export(devices, tags);
            return Results.File(
                bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"tags_{DateTime.Now:yyyyMMddHHmmss}.xlsx");
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/import", async (
            IFormFile file,
            IDbContextFactory<ConfigDbContext> contextFactory,
            TagExcelService excel,
            ConfigReloader reloader,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            // Excel 导入可能涉及任意设备，要求全部区域的配置权限。
            if (!(await http.GetAccessAsync()).HasGlobal(Permission.Configure))
                return await Audit.ForbidAsync(http, "导入测点 Excel", "Excel 导入需要全部区域的配置权限");
            if (file.Length == 0 || file.Length > 20 * 1024 * 1024)
            {
                return EndpointHelpers.BadRequest("文件为空或超过 20 MB");
            }

            if (!Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return EndpointHelpers.BadRequest("仅支持 .xlsx 文件");
            }

            await using var stream = file.OpenReadStream();
            var result = await excel.ImportAsync(stream, contextFactory, cancellationToken, http.User.GetUserId(), http.User.GetUserName());
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(result);
        })
            .DisableAntiforgery()
            .RequireAuthorization(AccessPolicies.Configure);
    }

    private static Tag MapTag(TagRequest request) => new()
    {
        DeviceId = request.DeviceId,
        Name = request.Name?.Trim() ?? "",
        Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
        Area = request.Area,
        Address = request.Address,
        DbNumber = request.DbNumber, BitOffset = request.BitOffset,
        NodeId = request.NodeId ?? "",
        DataType = request.DataType,
        Scale = request.Scale,
        Offset = request.Offset,
        Unit = string.IsNullOrWhiteSpace(request.Unit) ? null : request.Unit.Trim(),
        Writable = request.Writable,
        ArchiveMode = request.ArchiveMode,
        Deadband = request.Deadband,
        ArchivePeriodSec = request.ArchivePeriodSec,
        MinValue = request.MinValue,
        MaxValue = request.MaxValue
    };
}
