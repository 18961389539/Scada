using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
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
    double? MaxValue, int DbNumber = 1, int BitOffset = 0, string NodeId = "", int? ScanRateMs = null);

// 批量建点沿用单条测点的校验规则，由服务端一次性校验并原子写入。
public sealed record TagBatchItem(
    string Name,
    TagArea Area,
    int Address,
    TagDataType DataType,
    string? Description = null,
    int DbNumber = 1,
    int BitOffset = 0,
    string NodeId = "",
    double Scale = 1,
    double Offset = 0,
    string? Unit = null,
    bool Writable = false,
    ArchiveMode ArchiveMode = ArchiveMode.OnChange,
    double Deadband = 0,
    int ArchivePeriodSec = 600,
    double? MinValue = null,
    double? MaxValue = null,
    int? ScanRateMs = null);

public sealed record TagBatchRequest(int DeviceId, List<TagBatchItem>? Tags);

public static class TagEndpoints
{
    private static int[]? ParseIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ids = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : 0).Where(id => id > 0).Distinct().Take(500).ToArray();
        return ids.Length == 0 ? [0] : ids;
    }

    public static void MapTagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tags").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (
            int? deviceId,
            string? search,
            string? ids,
            string? deviceIds,
            string? quality,
            string? sort,
            int? skip,
            int? take,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            IDbContextFactory<RuntimeDbContext> runtime,
            IRealtimeCache cache,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var query = db.Tags.AsNoTracking();
            if (deviceId is int id) query = query.Where(x => x.DeviceId == id);
            var only = ParseIds(ids);
            if (only is { Length: > 0 }) query = query.Where(x => only.Contains(x.Id));
            var devices = ParseIds(deviceIds);
            if (devices is { Length: > 0 }) query = query.Where(x => x.DeviceId != null && devices.Contains(x.DeviceId.Value));

            if (!string.IsNullOrWhiteSpace(search))
            {
                var keyword = search.Trim();
                var namedDevices = await db.Devices.AsNoTracking().Where(x => x.Name.Contains(keyword)).Select(x => x.Id).ToListAsync(cancellationToken);
                query = query.Where(x => x.Name.Contains(keyword) || (x.Description != null && x.Description.Contains(keyword))
                    || (x.DeviceId != null && namedDevices.Contains(x.DeviceId.Value)));
            }

            var access = await http.GetAccessAsync();
            var tags = (await query.OrderBy(x => x.DeviceId).ThenBy(x => x.Address).ToListAsync(cancellationToken))
                .Where(x => access.CanTag(Permission.View, x.Id)).ToList();
            if (take is not int pageSize || pageSize <= 0) return Results.Ok(tags);

            pageSize = Math.Clamp(pageSize, 1, 500);
            var abnormal = tags.Count(tag => cache.Get(tag.Id) is { Quality: not TagQuality.Good });
            var scoped = quality is "good" or "abnormal"
                ? tags.Where(tag =>
                {
                    var sample = cache.Get(tag.Id);
                    if (sample is null) return false;
                    return quality == "good" ? sample.Quality == TagQuality.Good : sample.Quality != TagQuality.Good;
                }).ToList()
                : tags;
            if (sort == "abnormal")
            {
                var scopedIds = scoped.Select(tag => tag.Id).ToArray();
                await using var operational = await runtime.CreateDbContextAsync(cancellationToken);
                var alarms = await operational.AlarmEvents.AsNoTracking()
                    .Where(alarm => alarm.EndTs == null && alarm.TagId != null && scopedIds.Contains(alarm.TagId.Value))
                    .Select(alarm => new { TagId = alarm.TagId!.Value, alarm.Level })
                    .ToListAsync(cancellationToken);
                var rank = alarms.GroupBy(alarm => alarm.TagId).ToDictionary(group => group.Key, group => group.Any(alarm => alarm.Level == AlarmLevel.Critical) ? 4 : 3);
                int Rank(Tag tag) => rank.TryGetValue(tag.Id, out var alarmRank) ? alarmRank : cache.Get(tag.Id) is { Quality: not TagQuality.Good } ? 2 : 0;
                scoped = scoped.OrderByDescending(Rank).ThenBy(tag => tag.Id).ToList();
            }

            http.Response.Headers["X-Total-Count"] = scoped.Count.ToString();
            http.Response.Headers["X-Abnormal-Count"] = abnormal.ToString();
            return Results.Ok(scoped.Skip(Math.Max(0, skip ?? 0)).Take(pageSize).ToList());
        });

        group.MapGet("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
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
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            TagCommandService tags,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DeviceId, cancellationToken);
            if (device is null)
            {
                return EndpointHelpers.BadRequest($"设备不存在：{request.DeviceId}");
            }

            if (await EndpointHelpers.RequireAreaAsync(http, device.AreaId, Permission.Configure, $"创建测点：{request.Name}") is { } denied) return denied;

            // 审计行由 AuditInterceptor 在保存后自动写入（Tag 实现了 IAuditedEntity）。
            var result = await tags.CreateAsync(db, MapTag(request), device, cancellationToken);
            return result.Success
                ? Results.Created($"/api/tags/{result.Tag!.Id}", result.Tag)
                : EndpointHelpers.BadRequest(result.Message);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/batch", async (
            TagBatchRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            ConfigReloader reloader, TagCommandService tagCommands,
            CancellationToken cancellationToken) =>
        {
            if (request.Tags is null || request.Tags.Count == 0)
            {
                return EndpointHelpers.BadRequest("请至少生成一个测点");
            }

            if (request.Tags.Count > 500)
            {
                return EndpointHelpers.BadRequest("单次批量建点最多 500 个");
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DeviceId, cancellationToken);
            if (device is null)
            {
                return EndpointHelpers.BadRequest($"设备不存在：{request.DeviceId}");
            }

            if (await EndpointHelpers.RequireAreaAsync(http, device.AreaId, Permission.Configure, $"批量创建测点：{device.Name}") is { } denied) return denied;

            var tags = new List<Tag>(request.Tags.Count);
            for (var i = 0; i < request.Tags.Count; i++)
            {
                var item = request.Tags[i];
                var tag = MapTag(new TagRequest(
                    request.DeviceId, item.Name, item.Description, item.Area, item.Address, item.DataType,
                    item.Scale, item.Offset, item.Unit, item.Writable, item.ArchiveMode, item.Deadband,
                    item.ArchivePeriodSec, item.MinValue, item.MaxValue, item.DbNumber, item.BitOffset, item.NodeId, item.ScanRateMs));
                var error = ConfigurationValidator.Tag(tag, device);
                if (error is not null)
                {
                    return EndpointHelpers.BadRequest($"第 {i + 1} 个测点“{tag.Name}”：{error}");
                }

                tags.Add(tag);
            }

            var duplicated = tags.GroupBy(x => x.Name).Where(g => g.Count() > 1).Select(g => g.Key).Take(5).ToArray();
            if (duplicated.Length > 0)
            {
                return EndpointHelpers.BadRequest($"测点名称重复：{string.Join("、", duplicated)}");
            }

            if (await tagCommands.ValidateLaneBudgetAsync(db, device.Id, tags.Select(x => x.ScanRateMs), null, cancellationToken) is string laneError)
            {
                return EndpointHelpers.BadRequest(laneError);
            }

            var names = tags.Select(x => x.Name).ToArray();
            var existing = await db.Tags.AsNoTracking()
                .Where(x => x.DeviceId == device.Id && names.Contains(x.Name))
                .Select(x => x.Name)
                .Take(20)
                .ToListAsync(cancellationToken);
            if (existing.Count > 0)
            {
                return EndpointHelpers.BadRequest($"测点名称已存在：{string.Join("、", existing)}");
            }

            // 批量建点最多 500 个：逐条审计会淹没记录，改为保留下面这一条汇总。
            using var batchAudit = Scada.Core.Security.AuditContext.Suppress();
            db.Tags.AddRange(tags);
            runtimeDb.OperationLogs.Add(Audit.Change(http, $"批量创建测点：{device.Name}（{tags.Count} 个）", "Tag", null, null,
                new { deviceId = device.Id, count = tags.Count, names = tags.Select(x => x.Name).Take(50) }));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await runtimeDb.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return EndpointHelpers.BadRequest("测点名称与现有配置冲突，请调整名称后重试");
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(new { created = tags.Count });
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id,
            TagRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            TagCommandService tags,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var tag = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (tag is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireTagsAsync(http, [id], Permission.Configure, $"修改测点 #{id}") is { } denied) return denied;

            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.DeviceId, cancellationToken);
            if (device is null)
            {
                return EndpointHelpers.BadRequest($"设备不存在：{request.DeviceId}");
            }

            if (await EndpointHelpers.RequireAreaAsync(http, device.AreaId, Permission.Configure, $"修改测点 #{id} 归属设备") is { } deniedTarget) return deniedTarget;
            var updated = MapTag(request);
            updated.Id = id;
            var result = await tags.UpdateAsync(db, tag, updated, device, cancellationToken);
            return result.Success ? Results.Ok(result.Tag) : EndpointHelpers.BadRequest(result.Message);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            TagCommandService tags,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var tag = await db.Tags.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (tag is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireTagsAsync(http, [id], Permission.Configure, $"删除测点 #{id}") is { } denied) return denied;

            var result = await tags.DeleteAsync(db, tag, cancellationToken);
            if (result.Success) return Results.NoContent();
            return result.Error == TagCommandError.Conflict
                ? Results.Conflict(new { message = result.Message })
                : EndpointHelpers.BadRequest(result.Message);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapGet("/export", async (
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
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
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
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
            var result = await excel.ImportAsync(stream, contextFactory, runtimeFactory, cancellationToken, http.User.GetUserId(), http.User.GetUserName());
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
        MaxValue = request.MaxValue,
        ScanRateMs = request.ScanRateMs is int rate && rate > 0 ? rate : null
    };
}
