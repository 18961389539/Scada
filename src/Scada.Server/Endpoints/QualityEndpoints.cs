using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record QualityDefectCodeRequest(string Code, string Name, string? Category, string? Description, bool Enabled = true);
public sealed record QualityInspectionRequest(
    string? Sn, string? BatchNo, string? Product, int AreaId, int? DeviceId, int? TagId,
    QualityResult Result, int? DefectCodeId, string? Inspector, long? Ts, string? Note);

public sealed record QualityDefectCodeView(int Id, string Code, string Name, string Category, bool Enabled, string Description);
public sealed record QualityInspectionView(
    int Id, string Sn, string BatchNo, string Product, int AreaId, int? DeviceId, int? TagId,
    QualityResult Result, int? DefectCodeId, string? DefectCode, string? DefectName, string? DefectCategory,
    string Inspector, long Ts, string Note, string CreatedBy, string? DeviceName, string? TagName);
public sealed record QualityDayPoint(string Date, int Total, int Passed, int Failed, int Rework, int Scrap, double? Yield);
public sealed record QualityDefectPoint(int? DefectCodeId, string Code, string Name, string Category, int Count);
public sealed record QualityAreaPoint(int AreaId, string AreaName, int Total, int Passed, int Failed, double? Yield);
public sealed record QualityDevicePoint(int DeviceId, string DeviceName, int Total, int Passed, int Failed, double? Yield);
public sealed record QualitySummaryView(
    int Total, int Passed, int Failed, int Rework, int Scrap,
    double? Yield, double? ScrapRate, double? ReworkRate, double? DefectRate,
    IReadOnlyList<QualityDayPoint> ByDay, IReadOnlyList<QualityDefectPoint> ByDefect,
    IReadOnlyList<QualityAreaPoint> ByArea, IReadOnlyList<QualityDevicePoint> ByDevice);

public static class QualityEndpoints
{
    private static readonly JsonSerializerOptions CopyOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void MapQualityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quality").AddEndpointFilter<AuditFailuresFilter>().RequireAuthorization(AccessPolicies.View);

        MapDefectCodes(group);
        MapInspections(group);
    }

    private static void MapDefectCodes(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/defect-codes");

        group.MapGet("", async (bool? enabled, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.QualityDefectCodes.AsNoTracking();
            if (enabled is bool flag) query = query.Where(x => x.Enabled == flag);
            var rows = await query.OrderBy(x => x.Code).ToListAsync(ct);
            return Results.Ok(rows.Select(View).ToList());
        });

        group.MapPost("", async (QualityDefectCodeRequest request, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var now = ScadaTime.NowMs();
            var code = new QualityDefectCode
            {
                Code = request.Code?.Trim() ?? "", Name = request.Name?.Trim() ?? "",
                Category = request.Category?.Trim() ?? "", Enabled = request.Enabled,
                Description = request.Description?.Trim() ?? "", CreatedTs = now, UpdatedTs = now
            };
            var error = QualityValidation.DefectCode(code);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.QualityDefectCodes.AnyAsync(x => x.Code == code.Code, ct)) return EndpointHelpers.BadRequest("缺陷代码已存在");
            db.QualityDefectCodes.Add(code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("缺陷代码已存在"); }
            runtime.OperationLogs.Add(Audit.Change(http, $"创建缺陷代码：{code.Code}", "QualityDefectCode", code.Id, null, code));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Created($"/api/quality/defect-codes/{code.Id}", View(code));
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (int id, QualityDefectCodeRequest request, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var code = await db.QualityDefectCodes.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (code is null) return Results.NotFound();
            var before = JsonCopy(code);
            code.Code = request.Code?.Trim() ?? ""; code.Name = request.Name?.Trim() ?? "";
            code.Category = request.Category?.Trim() ?? ""; code.Enabled = request.Enabled;
            code.Description = request.Description?.Trim() ?? ""; code.UpdatedTs = ScadaTime.NowMs();
            var error = QualityValidation.DefectCode(code);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.QualityDefectCodes.AnyAsync(x => x.Id != id && x.Code == code.Code, ct))
                return EndpointHelpers.BadRequest("缺陷代码已存在");
            runtime.OperationLogs.Add(Audit.Change(http, $"修改缺陷代码：{code.Code}", "QualityDefectCode", code.Id, before, code));
            try { await db.SaveChangesAsync(ct); await runtime.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("缺陷代码已存在"); }
            return Results.Ok(View(code));
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (int id, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var code = await db.QualityDefectCodes.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (code is null) return Results.NotFound();
            if (await runtime.QualityInspections.AnyAsync(x => x.DefectCodeId == id, ct))
                return EndpointHelpers.BadRequest("该缺陷代码已有检验记录，请停用而不是删除");
            db.QualityDefectCodes.Remove(code);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除缺陷代码：{code.Code}", "QualityDefectCode", code.Id, code, null));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);
    }

    private static void MapInspections(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/inspections");

        group.MapGet("", async (
            string? search, string? sn, string? batchNo, string? product, QualityResult? result, int? defectCodeId,
            int? areaId, int? deviceId, long? from, long? to, int? page, int? pageSize,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var now = ScadaTime.NowMs();
            var start = from ?? now - 7L * 24 * 3600_000;
            var end = to ?? now;
            if (start >= end) return EndpointHelpers.BadRequest("时间范围无效");
            if (end - start > 365L * 24 * 3600_000) return EndpointHelpers.BadRequest("时间范围不能超过 365 天");
            var query = runtime.QualityInspections.AsNoTracking().Where(x => x.Ts >= start && x.Ts <= end);
            if (access.AreasWith(Permission.View) is { } visible)
            {
                var visibleAreas = visible.ToArray();
                query = query.Where(x => visibleAreas.Contains(x.AreaId));
            }
            if (areaId is int area) query = query.Where(x => x.AreaId == area);
            if (deviceId is int device) query = query.Where(x => x.DeviceId == device);
            if (result is QualityResult inspectionResult) query = query.Where(x => x.Result == inspectionResult);
            if (defectCodeId is int code) query = query.Where(x => x.DefectCodeId == code);
            if (!string.IsNullOrWhiteSpace(sn)) query = query.Where(x => x.Sn == sn.Trim());
            if (!string.IsNullOrWhiteSpace(batchNo)) query = query.Where(x => x.BatchNo == batchNo.Trim());
            if (!string.IsNullOrWhiteSpace(product)) query = query.Where(x => x.Product == product.Trim());
            var keyword = search?.Trim();
            if (keyword is { Length: > 0 })
                query = query.Where(x => (x.Sn + " " + x.BatchNo + " " + x.Product + " " + x.Inspector + " " + x.Note).Contains(keyword));
            var currentPage = Math.Clamp(page ?? 1, 1, 100000);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(x => x.Ts).ThenByDescending(x => x.Id)
                .Skip((currentPage - 1) * size).Take(size).ToListAsync(ct);
            var views = await ViewManyAsync(db, rows, ct);
            return Results.Ok(new { total, page = currentPage, pageSize = size, items = views });
        });

        app.MapGet("/summary", async (
            int? areaId, int? deviceId, string? product, string? batchNo, long? from, long? to,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var now = ScadaTime.NowMs();
            var start = from ?? now - 7L * 24 * 3600_000;
            var end = to ?? now;
            if (start >= end) return EndpointHelpers.BadRequest("时间范围无效");
            if (end - start > 365L * 24 * 3600_000) return EndpointHelpers.BadRequest("时间范围不能超过 365 天");
            var query = runtime.QualityInspections.AsNoTracking().Where(x => x.Ts >= start && x.Ts <= end);
            if (access.AreasWith(Permission.View) is { } visible)
            {
                var visibleAreas = visible.ToArray();
                query = query.Where(x => visibleAreas.Contains(x.AreaId));
            }
            if (areaId is int area) query = query.Where(x => x.AreaId == area);
            if (deviceId is int device) query = query.Where(x => x.DeviceId == device);
            if (!string.IsNullOrWhiteSpace(product)) query = query.Where(x => x.Product == product.Trim());
            if (!string.IsNullOrWhiteSpace(batchNo)) query = query.Where(x => x.BatchNo == batchNo.Trim());
            // 汇总全部在 SQL 侧分组，避免把整段时间的检验明细拉进内存。
            var resultCounts = await query.GroupBy(x => x.Result)
                .Select(g => new { Result = g.Key, Count = g.Count() }).ToListAsync(ct);
            var total = resultCounts.Sum(x => x.Count);
            var passed = resultCounts.Where(x => x.Result == QualityResult.Passed).Sum(x => x.Count);
            var failed = resultCounts.Where(x => x.Result == QualityResult.Failed).Sum(x => x.Count);
            var rework = resultCounts.Where(x => x.Result == QualityResult.Rework).Sum(x => x.Count);
            var scrap = resultCounts.Where(x => x.Result == QualityResult.Scrap).Sum(x => x.Count);
            double? Rate(int count) => total > 0 ? count / (double)total : null;

            var offset = (long)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalMilliseconds;
            var dayRows = await query.GroupBy(x => (x.Ts + offset) / 86_400_000L)
                .Select(g => new
                {
                    Day = g.Key, Total = g.Count(),
                    Passed = g.Count(x => x.Result == QualityResult.Passed),
                    Failed = g.Count(x => x.Result == QualityResult.Failed),
                    Rework = g.Count(x => x.Result == QualityResult.Rework),
                    Scrap = g.Count(x => x.Result == QualityResult.Scrap)
                })
                .OrderBy(g => g.Day).ToListAsync(ct);
            var byDay = dayRows.Select(g => new QualityDayPoint(
                DateTimeOffset.FromUnixTimeMilliseconds(g.Day * 86_400_000L - offset).ToLocalTime().ToString("yyyy-MM-dd"),
                g.Total, g.Passed, g.Failed, g.Rework, g.Scrap,
                g.Total > 0 ? g.Passed / (double)g.Total : null)).ToList();

            var codes = await db.QualityDefectCodes.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var defectRows = await query.Where(x => x.Result != QualityResult.Passed)
                .GroupBy(x => x.DefectCodeId)
                .Select(g => new { DefectCodeId = g.Key, Count = g.Count() })
                .OrderByDescending(g => g.Count).ToListAsync(ct);
            var byDefect = defectRows.Select(g =>
            {
                var code = g.DefectCodeId is int id ? codes.GetValueOrDefault(id) : null;
                return new QualityDefectPoint(g.DefectCodeId, code?.Code ?? "", code?.Name ?? "未分类", code?.Category ?? "", g.Count);
            }).ToList();

            var areas = await db.Areas.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var areaRows = await query.GroupBy(x => x.AreaId)
                .Select(g => new
                {
                    AreaId = g.Key, Total = g.Count(),
                    Passed = g.Count(x => x.Result == QualityResult.Passed),
                    Failed = g.Count(x => x.Result == QualityResult.Failed)
                })
                .OrderByDescending(g => g.Total).ToListAsync(ct);
            var byArea = areaRows.Select(g => new QualityAreaPoint(g.AreaId, areas.GetValueOrDefault(g.AreaId, $"#{g.AreaId}"),
                g.Total, g.Passed, g.Failed, g.Total > 0 ? g.Passed / (double)g.Total : null)).ToList();

            var devices = await db.Devices.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var deviceRows = await query.Where(x => x.DeviceId != null).GroupBy(x => x.DeviceId!.Value)
                .Select(g => new
                {
                    DeviceId = g.Key, Total = g.Count(),
                    Passed = g.Count(x => x.Result == QualityResult.Passed),
                    Failed = g.Count(x => x.Result == QualityResult.Failed)
                })
                .OrderByDescending(g => g.Total).ToListAsync(ct);
            var byDevice = deviceRows.Select(g => new QualityDevicePoint(g.DeviceId, devices.GetValueOrDefault(g.DeviceId, $"#{g.DeviceId}"),
                g.Total, g.Passed, g.Failed, g.Total > 0 ? g.Passed / (double)g.Total : null)).ToList();

            return Results.Ok(new QualitySummaryView(total, passed, failed, rework, scrap,
                Rate(passed), Rate(scrap), Rate(rework), Rate(failed + rework + scrap),
                byDay, byDefect, byArea, byDevice));
        });

        group.MapPost("", async (QualityInspectionRequest request, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (await EndpointHelpers.RequireAreaAsync(http, request.AreaId, Permission.Control, "录入质量检验") is { } denied)
                return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            if (await ValidateReferencesAsync(db, http, request, ct) is { } error) return error;
            var now = ScadaTime.NowMs();
            var inspection = new QualityInspection
            {
                AreaId = request.AreaId, DeviceId = request.DeviceId, TagId = request.TagId,
                Result = request.Result, DefectCodeId = request.DefectCodeId, Ts = request.Ts ?? now,
                CreatedTs = now, UpdatedTs = now, CreatedBy = http.User.GetUserName()
            };
            Apply(inspection, request);
            var validation = QualityValidation.Inspection(inspection);
            if (validation is not null) return EndpointHelpers.BadRequest(validation);
            runtime.QualityInspections.Add(inspection);
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            runtime.OperationLogs.Add(Audit.Change(http, $"录入质量检验：{inspection.Sn}", "QualityInspection", inspection.Id, null, inspection));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Created($"/api/quality/inspections/{inspection.Id}",
                (await ViewManyAsync(db, [inspection], ct))[0]);
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapPut("/{id:int}", async (int id, QualityInspectionRequest request, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var inspection = await runtime.QualityInspections.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (inspection is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, inspection.AreaId, Permission.Configure, $"修改质量检验 #{id}") is { } denied)
                return denied;
            if (request.AreaId != inspection.AreaId &&
                await EndpointHelpers.RequireAreaAsync(http, request.AreaId, Permission.Configure, $"移动质量检验 #{id}") is { } deniedMove)
                return deniedMove;
            if (await ValidateReferencesAsync(db, http, request, ct) is { } error) return error;
            var before = JsonCopy(inspection);
            Apply(inspection, request);
            inspection.AreaId = request.AreaId; inspection.DeviceId = request.DeviceId; inspection.TagId = request.TagId;
            inspection.Result = request.Result; inspection.DefectCodeId = request.DefectCodeId;
            inspection.Ts = request.Ts ?? inspection.Ts; inspection.UpdatedTs = ScadaTime.NowMs();
            var validation = QualityValidation.Inspection(inspection);
            if (validation is not null) return EndpointHelpers.BadRequest(validation);
            runtime.OperationLogs.Add(Audit.Change(http, $"修改质量检验：{inspection.Sn}", "QualityInspection", inspection.Id, before, inspection));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Ok((await ViewManyAsync(db, [inspection], ct))[0]);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (int id, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var inspection = await runtime.QualityInspections.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (inspection is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, inspection.AreaId, Permission.Configure, $"删除质量检验 #{id}") is { } denied)
                return denied;
            runtime.QualityInspections.Remove(inspection);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除质量检验：{inspection.Sn}", "QualityInspection", inspection.Id, inspection, null));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);
    }

    private static async Task<IResult?> ValidateReferencesAsync(
        ConfigDbContext db, HttpContext http, QualityInspectionRequest request, CancellationToken ct)
    {
        if (request.Result != QualityResult.Passed && request.DefectCodeId is null)
            return EndpointHelpers.BadRequest("非合格记录必须选择缺陷代码");
        if (request.DefectCodeId is int codeId)
        {
            var code = await db.QualityDefectCodes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == codeId, ct);
            if (code is null || !code.Enabled) return EndpointHelpers.BadRequest("缺陷代码不存在或已停用");
        }
        if (request.DeviceId is int deviceId)
        {
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null) return EndpointHelpers.BadRequest("关联设备不存在");
            if (device.AreaId != request.AreaId) return EndpointHelpers.BadRequest("关联设备不属于所选区域");
        }
        if (request.TagId is int tagId)
        {
            var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tagId, ct);
            if (tag is null) return EndpointHelpers.BadRequest("关联测点不存在");
            var access = await http.GetAccessAsync();
            if (!access.CanTag(Permission.View, tagId)) return await Audit.ForbidAsync(http, "质量检验关联测点", "无权读取该测点");
            if (!access.Model.AreasOfTag(tagId).Contains(request.AreaId))
                return EndpointHelpers.BadRequest("关联测点不属于所选区域");
        }
        return null;
    }

    private static void Apply(QualityInspection inspection, QualityInspectionRequest request)
    {
        inspection.Sn = request.Sn?.Trim() ?? "";
        inspection.BatchNo = request.BatchNo?.Trim() ?? "";
        inspection.Product = request.Product?.Trim() ?? "";
        inspection.Inspector = request.Inspector?.Trim() ?? "";
        inspection.Note = request.Note?.Trim() ?? "";
    }

    private static async Task<List<QualityInspectionView>> ViewManyAsync(
        ConfigDbContext db, IReadOnlyList<QualityInspection> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var codeIds = rows.Where(x => x.DefectCodeId != null).Select(x => x.DefectCodeId!.Value).Distinct().ToArray();
        var deviceIds = rows.Where(x => x.DeviceId != null).Select(x => x.DeviceId!.Value).Distinct().ToArray();
        var tagIds = rows.Where(x => x.TagId != null).Select(x => x.TagId!.Value).Distinct().ToArray();
        var codes = await db.QualityDefectCodes.AsNoTracking().Where(x => codeIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var devices = await db.Devices.AsNoTracking().Where(x => deviceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var tags = await db.Tags.AsNoTracking().Where(x => tagIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        return rows.Select(x =>
        {
            var code = x.DefectCodeId is int id ? codes.GetValueOrDefault(id) : null;
            return new QualityInspectionView(x.Id, x.Sn, x.BatchNo, x.Product, x.AreaId, x.DeviceId, x.TagId,
                x.Result, x.DefectCodeId, code?.Code, code?.Name, code?.Category, x.Inspector, x.Ts, x.Note,
                x.CreatedBy, x.DeviceId is int device ? devices.GetValueOrDefault(device) : null,
                x.TagId is int tag ? tags.GetValueOrDefault(tag) : null);
        }).ToList();
    }

    private static QualityDefectCodeView View(QualityDefectCode code) =>
        new(code.Id, code.Code, code.Name, code.Category, code.Enabled, code.Description);

    private static JsonElement JsonCopy(QualityDefectCode value) => JsonSerializer.SerializeToElement(value, value.GetType(), CopyOptions);
    private static JsonElement JsonCopy(QualityInspection value) => JsonSerializer.SerializeToElement(value, value.GetType(), CopyOptions);
}
