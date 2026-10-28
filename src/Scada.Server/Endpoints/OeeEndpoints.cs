using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Oee;
using Scada.Server.Auth;
using Scada.Server.Localization;
using Scada.Server.Reports;

namespace Scada.Server.Endpoints;

public sealed record OeeThresholdsRequest(OeeThresholds Thresholds, long Revision = 0);
public sealed record OeeRollupRebuildRequest(int Days);
public sealed record ProductionLineRequest(string Name, int AreaId, string? Description, int? StateTagId, string? StateMapJson,
    int? GoodCountTagId, int? RejectCountTagId, int? TotalCountTagId, int? ProductId,
    double IdealCycleSec, double? RatedUnitsPerHour, double? TargetUnitsPerHour, bool Enabled, long Revision = 0);
public sealed record ProductionProductRequest(string Name, string? Code, int? AreaId, double IdealCycleSec, double? RatedUnitsPerHour, string? Description, long Revision = 0);
public sealed record ProductionShiftRequest(string Name, int AreaId, int StartMinute, int EndMinute, int Days, bool Enabled, long Revision = 0);
public sealed record DowntimeReasonRequest(string Code, string Name, int? AreaId, OeeLossCategory Category, bool IsPlanned, bool Enabled, long Revision = 0);
public sealed record DowntimeEventRequest(int LineId, long StartTs, long? EndTs, int? ReasonId, LineState State, string? Note, bool Confirmed = true);
public sealed record CountCorrectionRequest(int LineId, int? ProductId, long PeriodFrom, long PeriodTo, double GoodDelta, double RejectDelta, double ScrapDelta, string Reason);

public static class OeeEndpoints
{
    public const long MaxRangeMs = 366 * OeeService.DayMs;

    public static void MapOeeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/oee").RequireAuthorization().AddEndpointFilter<AuditFailuresFilter>();

        // ---- 配置：产线 / 产品 / 班次 / 原因 ----
        group.MapGet("/lines", async (HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok((await db.ProductionLines.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
                .Where(x => access.Has(Permission.View, x.AreaId)).Select(LineView));
        });
        group.MapPost("/lines", (ProductionLineRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveLine(null, r, http, f, ct));
        group.MapPut("/lines/{id:int}", (int id, ProductionLineRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveLine(id, r, http, f, ct));
        group.MapDelete("/lines/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.ProductionLines.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, row.AreaId)) return await Audit.ForbidAsync(http, $"删除产线 {row.Name}", "无该区域的配置权限", "ProductionLine", id);
            db.ProductionLines.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除产线 {row.Name}", "ProductionLine", id, LineView(row), null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/products", async (HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok((await db.ProductionProducts.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct))
                .Where(x => x.AreaId is null || access.Has(Permission.View, x.AreaId.Value)).Select(ProductView));
        });
        group.MapPost("/products", (ProductionProductRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveProduct(null, r, http, f, ct));
        group.MapPut("/products/{id:int}", (int id, ProductionProductRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveProduct(id, r, http, f, ct));
        group.MapDelete("/products/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.ProductionProducts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.AreaId is int a && !(await http.GetAccessAsync()).Has(Permission.Configure, a)) return await Audit.ForbidAsync(http, $"删除产品 {row.Name}", "无该区域的配置权限", "ProductionProduct", id);
            if (row.AreaId is null && !(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return await Audit.ForbidAsync(http, $"删除产品 {row.Name}", "全局产品需全局配置权限", "ProductionProduct", id);
            if (await db.ProductionLines.AnyAsync(x => x.ProductId == id, ct)) return Results.Conflict(new { message = "仍有产线引用该产品" });
            db.ProductionProducts.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除产品 {row.Name}", "ProductionProduct", id, ProductView(row), null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/shifts", async (HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok((await db.ProductionShifts.AsNoTracking().OrderBy(x => x.AreaId).ThenBy(x => x.StartMinute).ToListAsync(ct))
                .Where(x => access.Has(Permission.View, x.AreaId)).Select(ShiftView));
        });
        group.MapPost("/shifts", (ProductionShiftRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveShift(null, r, http, f, ct));
        group.MapPut("/shifts/{id:int}", (int id, ProductionShiftRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveShift(id, r, http, f, ct));
        group.MapDelete("/shifts/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.ProductionShifts.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Configure, row.AreaId)) return await Audit.ForbidAsync(http, $"删除班次 {row.Name}", "无该区域的配置权限", "ProductionShift", id);
            db.ProductionShifts.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除班次 {row.Name}", "ProductionShift", id, ShiftView(row), null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/reasons", async (HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await f.CreateDbContextAsync(ct);
            return Results.Ok((await db.DowntimeReasons.AsNoTracking().OrderBy(x => x.Code).ToListAsync(ct))
                .Where(x => x.AreaId is null || access.Has(Permission.View, x.AreaId.Value)).Select(ReasonView));
        });
        group.MapPost("/reasons", (DowntimeReasonRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveReason(null, r, http, f, ct));
        group.MapPut("/reasons/{id:int}", (int id, DowntimeReasonRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) => SaveReason(id, r, http, f, ct));
        group.MapDelete("/reasons/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.DowntimeReasons.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            if (row.AreaId is int a && !(await http.GetAccessAsync()).Has(Permission.Configure, a)) return await Audit.ForbidAsync(http, $"删除停机原因 {row.Code}", "无该区域的配置权限", "DowntimeReason", id);
            if (row.AreaId is null && !(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return await Audit.ForbidAsync(http, $"删除停机原因 {row.Code}", "全局原因需全局配置权限", "DowntimeReason", id);
            db.DowntimeReasons.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除停机原因 {row.Code}", "DowntimeReason", id, ReasonView(row), null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---- 停机事件 / 产量修正 ----
        group.MapGet("/downtimes", async (long? from, long? to, int? lineId, bool? confirmed, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            var now = ScadaTime.NowMs();
            var toMs = to ?? now; var fromMs = from ?? toMs - OeeService.DayMs;
            if (fromMs >= toMs || (double)toMs - fromMs > 366d * OeeService.DayMs) return EndpointHelpers.BadRequest("请选择有效且不超过 366 天的时间范围");
            await using var db = await f.CreateDbContextAsync(ct);
            var lines = (await db.ProductionLines.AsNoTracking().ToListAsync(ct)).Where(x => access.Has(Permission.View, x.AreaId)).ToDictionary(x => x.Id);
            var lineIds = lines.Keys.ToArray();
            var q = db.DowntimeEvents.AsNoTracking().Where(x => lineIds.Contains(x.LineId) && x.StartTs < toMs && (x.EndTs == null || x.EndTs > fromMs));
            if (lineId is int l) q = q.Where(x => x.LineId == l);
            if (confirmed is bool c) q = q.Where(x => x.Confirmed == c);
            var rows = await q.OrderByDescending(x => x.StartTs).ThenByDescending(x => x.Id).Take(2000).ToListAsync(ct);
            return Results.Ok(rows.Select(e => DowntimeView(e, lines.GetValueOrDefault(e.LineId)?.Name)));
        });
        group.MapPost("/downtimes", async (DowntimeEventRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var line = await db.ProductionLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.LineId, ct);
            if (line is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Control, line.AreaId)) return await Audit.ForbidAsync(http, "录入停机", "无该区域的控制权限", "DowntimeEvent", null);
            if (r.EndTs is long e && e <= r.StartTs) return EndpointHelpers.BadRequest("结束时间须晚于开始时间");
            var row = new DowntimeEvent
            {
                LineId = r.LineId, StartTs = r.StartTs, EndTs = r.EndTs, ReasonId = r.ReasonId, State = r.State,
                Source = "manual", Note = Trunc(r.Note, 500), Confirmed = r.Confirmed,
                CreatedBy = http.User.GetUserName(), CreatedTs = ScadaTime.NowMs()
            };
            db.DowntimeEvents.Add(row);
            db.OperationLogs.Add(Audit.Change(http, $"录入停机 产线{line.Name}", "DowntimeEvent", null, null, DowntimeView(row, line.Name)));
            await db.SaveChangesAsync(ct);
            return Results.Ok(DowntimeView(row, line.Name));
        });
        group.MapPut("/downtimes/{id:long}", async (long id, DowntimeEventRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, OeeRollupService rollups, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.DowntimeEvents.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            var line = await db.ProductionLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.LineId, ct);
            if (line is null || !(await http.GetAccessAsync()).Has(Permission.Control, line.AreaId)) return await Audit.ForbidAsync(http, "修改停机", "无该区域的控制权限", "DowntimeEvent", id);
            if (r.LineId != row.LineId) return EndpointHelpers.BadRequest("不能通过修改停机事件切换产线");
            if (r.EndTs is long end && end <= r.StartTs) return EndpointHelpers.BadRequest("结束时间须晚于开始时间");
            var before = DowntimeView(row, line.Name);
            row.StartTs = r.StartTs; row.EndTs = r.EndTs; row.ReasonId = r.ReasonId; row.State = r.State; row.Note = Trunc(r.Note, 500);
            row.Confirmed = r.Confirmed; row.UpdatedBy = http.User.GetUserName(); row.UpdatedTs = ScadaTime.NowMs();
            db.OperationLogs.Add(Audit.Change(http, $"修改停机 产线{line.Name}", "DowntimeEvent", id, before, DowntimeView(row, line.Name)));
            await db.SaveChangesAsync(ct);
            await rollups.RequestRebuildAsync(row.StartTs, ct);
            return Results.Ok(DowntimeView(row, line.Name));
        });
        group.MapPost("/downtimes/{id:long}/confirm", async (long id, HttpContext http, IDbContextFactory<ConfigDbContext> f, OeeRollupService rollups, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.DowntimeEvents.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            var line = await db.ProductionLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.LineId, ct);
            if (line is null || !(await http.GetAccessAsync()).Has(Permission.Control, line.AreaId)) return await Audit.ForbidAsync(http, "确认停机", "无该区域的控制权限", "DowntimeEvent", id);
            row.Confirmed = true; row.UpdatedBy = http.User.GetUserName(); row.UpdatedTs = ScadaTime.NowMs();
            db.OperationLogs.Add(Audit.Change(http, $"确认停机候选 #{id}", "DowntimeEvent", id, new { confirmed = false }, new { confirmed = true }));
            await db.SaveChangesAsync(ct);
            await rollups.RequestRebuildAsync(row.StartTs, ct);
            return Results.Ok(DowntimeView(row, line.Name));
        });
        group.MapDelete("/downtimes/{id:long}", async (long id, HttpContext http, IDbContextFactory<ConfigDbContext> f, OeeRollupService rollups, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.DowntimeEvents.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (row is null) return Results.NotFound();
            var line = await db.ProductionLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.LineId, ct);
            if (line is null || !(await http.GetAccessAsync()).Has(Permission.Control, line.AreaId)) return await Audit.ForbidAsync(http, "删除停机", "无该区域的控制权限", "DowntimeEvent", id);
            db.DowntimeEvents.Remove(row);
            db.OperationLogs.Add(Audit.Change(http, $"删除停机 #{id}", "DowntimeEvent", id, DowntimeView(row, line.Name), null));
            await db.SaveChangesAsync(ct);
            await rollups.RequestRebuildAsync(row.StartTs, ct);
            return Results.NoContent();
        });
        group.MapPost("/downtimes/import-alarms", async (long? from, long? to, HttpContext http, OeeCollectorService collector, CancellationToken ct) =>
        {
            if (!(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return await Audit.ForbidAsync(http, "导入报警停机候选", "需全局配置权限");
            var now = ScadaTime.NowMs();
            var n = await collector.ImportAlarmCandidatesAsync(from ?? now - OeeService.DayMs, to ?? now, ct);
            return Results.Ok(new { imported = n });
        });

        group.MapPost("/corrections", async (CountCorrectionRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, OeeRollupService rollups, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var line = await db.ProductionLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.LineId, ct);
            if (line is null) return Results.NotFound();
            if (!(await http.GetAccessAsync()).Has(Permission.Control, line.AreaId)) return await Audit.ForbidAsync(http, "产量修正", "无该区域的控制权限", "ProductionCountCorrection", null);
            if (r.PeriodTo <= r.PeriodFrom) return EndpointHelpers.BadRequest("结束时间须晚于开始时间");
            if (string.IsNullOrWhiteSpace(r.Reason)) return EndpointHelpers.BadRequest("须填写修正原因");
            var row = new ProductionCountCorrection
            {
                LineId = r.LineId, ProductId = r.ProductId, PeriodFrom = r.PeriodFrom, PeriodTo = r.PeriodTo,
                GoodDelta = r.GoodDelta, RejectDelta = r.RejectDelta, ScrapDelta = r.ScrapDelta,
                Reason = Trunc(r.Reason, 500)!, CreatedBy = http.User.GetUserName(), CreatedTs = ScadaTime.NowMs()
            };
            db.ProductionCountCorrections.Add(row);
            db.OperationLogs.Add(Audit.Change(http, $"产量修正 产线{line.Name}", "ProductionCountCorrection", null, null, row));
            await db.SaveChangesAsync(ct);
            await rollups.RequestRebuildAsync(r.PeriodFrom, ct);
            return Results.Ok(row);
        });

        // ---- 分析 / 导出 / 阈值 / 预聚合 ----
        group.MapGet("/analytics", async (string? from, string? to, int? areaId, int? lineId, int? productId, int? shiftId,
            HttpContext http, IDbContextFactory<ConfigDbContext> f, OeeService oee, CancellationToken ct) =>
        {
            var filter = await FilterAsync(http, f, from, to, areaId, lineId, ct);
            if (filter.Error is not null) return filter.Error;
            var th = (await oee.GetThresholdsAsync(ct)).Thresholds;
            return Results.Ok(await oee.AnalyzeAsync(filter.From, filter.To, filter.LineIds, productId, shiftId, ScadaTime.NowMs(), th, ct));
        });
        group.MapGet("/export", async (string? format, string? from, string? to, int? areaId, int? lineId, int? productId, int? shiftId,
            HttpContext http, IDbContextFactory<ConfigDbContext> f, OeeService oee, Microsoft.Extensions.Options.IOptions<Scada.Core.Options.ScadaSettings> options, CancellationToken ct) =>
        {
            var filter = await FilterAsync(http, f, from, to, areaId, lineId, ct);
            if (filter.Error is not null) return filter.Error;
            var th = (await oee.GetThresholdsAsync(ct)).Thresholds;
            var report = await oee.AnalyzeAsync(filter.From, filter.To, filter.LineIds, productId, shiftId, ScadaTime.NowMs(), th, ct);
            var locale = Localizer.RequestLocale(http);
            var title = Localizer.T("OEE 生产统计", locale);
            var sections = OeeExport.Sections(report, oee.Zone, locale);
            var stamp = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, oee.Zone).ToString("yyyy-MM-dd HH:mm:ss");
            var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
            var bytes = pdf
                ? OeeExport.Pdf(title, sections, options.Value.ReportFontPath, Localizer.T($"生成于 {stamp}", locale))
                : OeeExport.Xlsx(title, sections);
            await using var db = await f.CreateDbContextAsync(ct);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(),
                Action = "导出 OEE 分析", Result = "成功", TargetType = "Oee",
                Details = JsonSerializer.Serialize(new { format = pdf ? "pdf" : "xlsx", from = filter.From, to = filter.To, areaId, lineId })
            });
            await db.SaveChangesAsync(ct);
            var fileName = $"oee-{DateTime.UtcNow:yyyyMMddHHmmss}.{(pdf ? "pdf" : "xlsx")}";
            return Results.File(bytes, pdf ? "application/pdf" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        });
        group.MapGet("/thresholds", async (OeeService oee, CancellationToken ct) =>
        {
            var (th, rev) = await oee.GetThresholdsAsync(ct);
            return Results.Ok(new { thresholds = th, revision = rev, defaults = oee.Defaults() });
        });
        group.MapPut("/thresholds", async (OeeThresholdsRequest req, HttpContext http, IDbContextFactory<ConfigDbContext> f, OeeService oee, CancellationToken ct) =>
        {
            if (!(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return await Audit.ForbidAsync(http, "修改 OEE 阈值", "需全局配置权限");
            if (req.Thresholds.Validate() is string err) return EndpointHelpers.BadRequest(err);
            await using var db = await f.CreateDbContextAsync(ct);
            var row = await db.OeeConfigs.FirstOrDefaultAsync(x => x.Id == 1, ct);
            if (row is null) { row = new OeeConfig { Id = 1 }; db.OeeConfigs.Add(row); }
            if (req.Revision != 0 && row.Revision != req.Revision) return Results.Conflict(new { message = "阈值已被他人修改", revision = row.Revision });
            var before = oee.Parse(row.ThresholdsJson);
            row.ThresholdsJson = OeeService.Serialize(req.Thresholds);
            row.Revision++; row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
            db.OperationLogs.Add(Audit.Change(http, "修改 OEE 阈值", "OeeConfig", 1, before, req.Thresholds));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { thresholds = req.Thresholds, revision = row.Revision });
        });
        group.MapGet("/rollups", async (IDbContextFactory<ConfigDbContext> f, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var state = await db.OeeRollupStates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
            var minutes = await db.OeeRollups.CountAsync(x => x.Granularity == OeeRollup.Minute, ct);
            var hours = await db.OeeRollups.CountAsync(x => x.Granularity == OeeRollup.Hour, ct);
            var days = await db.OeeRollups.CountAsync(x => x.Granularity == OeeRollup.Day, ct);
            return Results.Ok(new { state, minutes, hours, days });
        });
        group.MapPost("/rollups/rebuild", async (OeeRollupRebuildRequest req, HttpContext http, OeeRollupService rollups, CancellationToken ct) =>
        {
            if (!(await http.GetAccessAsync()).HasGlobal(Permission.Configure)) return await Audit.ForbidAsync(http, "重算 OEE 预聚合", "需全局配置权限");
            var days = Math.Clamp(req.Days, 1, 400);
            var from = ScadaTime.NowMs() - days * OeeService.DayMs;
            await rollups.RequestRebuildAsync(from, ct);
            return Results.Ok(new { scheduledFrom = from, days });
        });
    }

    private static async Task<(long From, long To, List<int> LineIds, IResult? Error)> FilterAsync(
        HttpContext http, IDbContextFactory<ConfigDbContext> f, string? from, string? to, int? areaId, int? lineId, CancellationToken ct)
    {
        var access = await http.GetAccessAsync();
        var now = ScadaTime.NowMs();
        var toMs = EndpointHelpers.ParseTime(to, now);
        var fromMs = EndpointHelpers.ParseTime(from, toMs - OeeService.DayMs);
        if (toMs <= fromMs) return (0, 0, [], EndpointHelpers.BadRequest("结束时间须晚于开始时间"));
        if (toMs - fromMs > MaxRangeMs) return (0, 0, [], EndpointHelpers.BadRequest("统计区间不能超过 366 天"));
        if (areaId is int a && !access.Has(Permission.View, a)) return (0, 0, [], Results.NotFound());
        await using var db = await f.CreateDbContextAsync(ct);
        var lines = await db.ProductionLines.AsNoTracking().ToListAsync(ct);
        var visible = lines.Where(x => access.Has(Permission.View, x.AreaId)
            && (areaId is null || x.AreaId == areaId)
            && (lineId is null || x.Id == lineId)).Select(x => x.Id).ToList();
        if (lineId is int lid && !visible.Contains(lid)) return (0, 0, [], Results.NotFound());
        return (fromMs, toMs, visible, null);
    }

    private static async Task<IResult> SaveLine(int? id, ProductionLineRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return EndpointHelpers.BadRequest("名称不能为空");
        if (r.IdealCycleSec <= 0 && r.RatedUnitsPerHour is not > 0) return EndpointHelpers.BadRequest("须设置理想节拍或额定产量");
        await using var db = await f.CreateDbContextAsync(ct);
        if (!(await http.GetAccessAsync()).Has(Permission.Configure, r.AreaId)) return await Audit.ForbidAsync(http, $"{(id is null ? "新建" : "修改")}产线", "无该区域的配置权限", "ProductionLine", id);
        var row = id is int i ? await db.ProductionLines.SingleOrDefaultAsync(x => x.Id == i, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (row is not null && r.Revision != 0 && row.Revision != r.Revision) return Results.Conflict(new { message = "产线已被他人修改", revision = row.Revision });
        if (await db.ProductionLines.AnyAsync(x => x.Name == r.Name.Trim() && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("产线名称已存在");
        object? before = row is null ? null : LineView(row);
        row ??= db.ProductionLines.Add(new ProductionLine()).Entity;
        row.Name = r.Name.Trim(); row.AreaId = r.AreaId; row.Description = Trunc(r.Description, 500);
        row.StateTagId = r.StateTagId; row.StateMapJson = string.IsNullOrWhiteSpace(r.StateMapJson) ? "{}" : r.StateMapJson!;
        row.GoodCountTagId = r.GoodCountTagId; row.RejectCountTagId = r.RejectCountTagId; row.TotalCountTagId = r.TotalCountTagId;
        row.ProductId = r.ProductId; row.IdealCycleSec = r.IdealCycleSec; row.RatedUnitsPerHour = r.RatedUnitsPerHour;
        row.TargetUnitsPerHour = r.TargetUnitsPerHour; row.Enabled = r.Enabled;
        row.Revision = (row.Revision <= 0 ? 1 : row.Revision) + (id is null ? 0 : 1); if (id is null) row.Revision = 1;
        row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
        db.OperationLogs.Add(Audit.Change(http, $"{(id is null ? "新建" : "修改")}产线 {row.Name}", "ProductionLine", row.Id, before, LineView(row)));
        await db.SaveChangesAsync(ct);
        return Results.Ok(LineView(row));
    }

    private static async Task<IResult> SaveProduct(int? id, ProductionProductRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return EndpointHelpers.BadRequest("名称不能为空");
        if (r.IdealCycleSec <= 0 && r.RatedUnitsPerHour is not > 0) return EndpointHelpers.BadRequest("须设置理想节拍或额定产量");
        var access = await http.GetAccessAsync();
        if (r.AreaId is int a) { if (!access.Has(Permission.Configure, a)) return await Audit.ForbidAsync(http, "保存产品", "无该区域的配置权限", "ProductionProduct", id); }
        else if (!access.HasGlobal(Permission.Configure)) return await Audit.ForbidAsync(http, "保存产品", "全局产品需全局配置权限", "ProductionProduct", id);
        await using var db = await f.CreateDbContextAsync(ct);
        var row = id is int i ? await db.ProductionProducts.SingleOrDefaultAsync(x => x.Id == i, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (row is not null && r.Revision != 0 && row.Revision != r.Revision) return Results.Conflict(new { message = "产品已被他人修改", revision = row.Revision });
        if (await db.ProductionProducts.AnyAsync(x => x.Name == r.Name.Trim() && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("产品名称已存在");
        object? before = row is null ? null : ProductView(row);
        row ??= db.ProductionProducts.Add(new ProductionProduct()).Entity;
        row.Name = r.Name.Trim(); row.Code = Trunc(r.Code, 60); row.AreaId = r.AreaId;
        row.IdealCycleSec = r.IdealCycleSec; row.RatedUnitsPerHour = r.RatedUnitsPerHour; row.Description = Trunc(r.Description, 500);
        row.Revision = id is null ? 1 : row.Revision + 1; row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
        db.OperationLogs.Add(Audit.Change(http, $"{(id is null ? "新建" : "修改")}产品 {row.Name}", "ProductionProduct", row.Id, before, ProductView(row)));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ProductView(row));
    }

    private static async Task<IResult> SaveShift(int? id, ProductionShiftRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return EndpointHelpers.BadRequest("名称不能为空");
        if (r.StartMinute is < 0 or > 1439 || r.EndMinute is < 0 or > 1439) return EndpointHelpers.BadRequest("班次起止分钟须为 0～1439");
        if (r.Days is < 1 or > 127) return EndpointHelpers.BadRequest("星期掩码无效");
        if (!(await http.GetAccessAsync()).Has(Permission.Configure, r.AreaId)) return await Audit.ForbidAsync(http, "保存班次", "无该区域的配置权限", "ProductionShift", id);
        await using var db = await f.CreateDbContextAsync(ct);
        var row = id is int i ? await db.ProductionShifts.SingleOrDefaultAsync(x => x.Id == i, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (row is not null && r.Revision != 0 && row.Revision != r.Revision) return Results.Conflict(new { message = "班次已被他人修改", revision = row.Revision });
        if (await db.ProductionShifts.AnyAsync(x => x.AreaId == r.AreaId && x.Name == r.Name.Trim() && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("同区域班次名称已存在");
        object? before = row is null ? null : ShiftView(row);
        row ??= db.ProductionShifts.Add(new ProductionShift()).Entity;
        row.Name = r.Name.Trim(); row.AreaId = r.AreaId; row.StartMinute = r.StartMinute; row.EndMinute = r.EndMinute; row.Days = r.Days; row.Enabled = r.Enabled;
        row.Revision = id is null ? 1 : row.Revision + 1; row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
        db.OperationLogs.Add(Audit.Change(http, $"{(id is null ? "新建" : "修改")}班次 {row.Name}", "ProductionShift", row.Id, before, ShiftView(row)));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ShiftView(row));
    }

    private static async Task<IResult> SaveReason(int? id, DowntimeReasonRequest r, HttpContext http, IDbContextFactory<ConfigDbContext> f, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Name)) return EndpointHelpers.BadRequest("代码与名称不能为空");
        var access = await http.GetAccessAsync();
        if (r.AreaId is int a) { if (!access.Has(Permission.Configure, a)) return await Audit.ForbidAsync(http, "保存停机原因", "无该区域的配置权限", "DowntimeReason", id); }
        else if (!access.HasGlobal(Permission.Configure)) return await Audit.ForbidAsync(http, "保存停机原因", "全局原因需全局配置权限", "DowntimeReason", id);
        await using var db = await f.CreateDbContextAsync(ct);
        var row = id is int i ? await db.DowntimeReasons.SingleOrDefaultAsync(x => x.Id == i, ct) : null;
        if (id is not null && row is null) return Results.NotFound();
        if (row is not null && r.Revision != 0 && row.Revision != r.Revision) return Results.Conflict(new { message = "原因已被他人修改", revision = row.Revision });
        if (await db.DowntimeReasons.AnyAsync(x => x.Code == r.Code.Trim() && x.Id != (id ?? 0), ct)) return EndpointHelpers.BadRequest("原因代码已存在");
        object? before = row is null ? null : ReasonView(row);
        row ??= db.DowntimeReasons.Add(new DowntimeReason()).Entity;
        row.Code = r.Code.Trim(); row.Name = r.Name.Trim(); row.AreaId = r.AreaId; row.Category = r.Category; row.IsPlanned = r.IsPlanned; row.Enabled = r.Enabled;
        row.Revision = id is null ? 1 : row.Revision + 1; row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
        db.OperationLogs.Add(Audit.Change(http, $"{(id is null ? "新建" : "修改")}停机原因 {row.Code}", "DowntimeReason", row.Id, before, ReasonView(row)));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ReasonView(row));
    }

    private static object LineView(ProductionLine x) => new
    {
        x.Id, x.Name, x.AreaId, x.Description, x.StateTagId, x.StateMapJson, x.GoodCountTagId, x.RejectCountTagId, x.TotalCountTagId,
        x.ProductId, x.IdealCycleSec, x.RatedUnitsPerHour, x.TargetUnitsPerHour, x.Enabled, x.Revision, x.UpdatedTs, x.UpdatedBy
    };
    private static object ProductView(ProductionProduct x) => new { x.Id, x.Name, x.Code, x.AreaId, x.IdealCycleSec, x.RatedUnitsPerHour, x.Description, x.Revision, x.UpdatedTs, x.UpdatedBy };
    private static object ShiftView(ProductionShift x) => new { x.Id, x.Name, x.AreaId, x.StartMinute, x.EndMinute, x.Days, x.Enabled, x.Revision, x.UpdatedTs, x.UpdatedBy };
    private static object ReasonView(DowntimeReason x) => new { x.Id, x.Code, x.Name, x.AreaId, category = x.Category.ToString(), x.IsPlanned, x.Enabled, x.Revision, x.UpdatedTs, x.UpdatedBy };
    private static object DowntimeView(DowntimeEvent e, string? lineName) => new
    {
        e.Id, e.LineId, lineName, startTs = e.StartTs, endTs = e.EndTs, e.ReasonId, state = e.State.ToString(), e.Source,
        e.AlarmEventId, e.Note, e.Confirmed, e.CreatedBy, e.CreatedTs, e.UpdatedBy, e.UpdatedTs
    };
    private static string? Trunc(string? s, int n) => s is null ? null : s.Length <= n ? s : s[..n];
}
