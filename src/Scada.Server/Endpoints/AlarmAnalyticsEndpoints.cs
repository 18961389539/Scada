using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Alarms;
using Scada.Server.Auth;
using Scada.Server.Localization;
using Scada.Server.Reports;

namespace Scada.Server.Endpoints;

public sealed record AlarmThresholdsRequest(AlarmAnalyticsThresholds Thresholds, long Revision = 0);
public sealed record AlarmRollupRebuildRequest(int Days);

// 报警统计分析：查看需 View（结果只含有权查看区域的报警）；阈值修改、预聚合重算需全局配置权限并记差分审计。
public static class AlarmAnalyticsEndpoints
{
    public const long MaxRangeMs = 366 * AlarmAnalyticsService.DayMs;

    // 过滤：可见性（区域授权）+ 指定区域 + 等级 + 规则。
    public static Func<AlarmSample, bool> Include(AccessContext access, int? areaId, IReadOnlySet<int>? levels, IReadOnlySet<int>? rules)
    {
        var model = access.Model;
        bool InArea(AlarmSample s, int area) =>
            s.TagId is int t ? model.AreasOfTag(t).Contains(area)
            : s.DeviceId is int d ? model.DeviceAreas.GetValueOrDefault(d, Area.DefaultId) == area
            : area == Area.DefaultId;
        var cache = new Dictionary<(int?, int?, int?), bool>();
        return s =>
        {
            if (levels is { Count: > 0 } && !levels.Contains((int)s.Level)) return false;
            if (rules is { Count: > 0 } && (s.RuleId is not int r || !rules.Contains(r))) return false;
            if (areaId is int a && !InArea(s, a)) return false;
            var key = (s.TagId, s.DeviceId, s.GatewayId);
            if (!cache.TryGetValue(key, out var ok))
                cache[key] = ok = EndpointHelpers.CanAlarm(access, Permission.View, new AlarmEvent { TagId = s.TagId, DeviceId = s.DeviceId, GatewayId = s.GatewayId });
            return ok;
        };
    }

    // 注意：不能用 EndpointHelpers.ParseIds —— 它会过滤 <= 0，而 AlarmLevel.Info = 0 是合法等级值，
    // 导致「只筛选提示级」被当成「不筛选」返回全部数据。
    private static HashSet<int>? Set(string? csv) => string.IsNullOrWhiteSpace(csv)
        ? null
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var value) ? value : -1)
            .Where(value => value >= 0)
            .ToHashSet();

    private static async Task<(long From, long To, Func<AlarmSample, bool> Include, IResult? Error)> FilterAsync(HttpContext http, string? from, string? to, int? areaId, string? levels, string? rules)
    {
        var access = await http.GetAccessAsync();
        var now = ScadaTime.NowMs();
        var toMs = EndpointHelpers.ParseTime(to, now);
        var fromMs = EndpointHelpers.ParseTime(from, toMs - AlarmAnalyticsService.DayMs);
        if (toMs <= fromMs) return (0, 0, _ => false, EndpointHelpers.BadRequest("结束时间须晚于开始时间"));
        if (toMs - fromMs > MaxRangeMs) return (0, 0, _ => false, EndpointHelpers.BadRequest("统计区间不能超过 366 天"));
        if (areaId is int a && !access.Has(Permission.View, a)) return (0, 0, _ => false, Results.NotFound());
        return (fromMs, toMs, Include(access, areaId, Set(levels), Set(rules)), null);
    }

    public static void MapAlarmAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/alarm-analytics").RequireAuthorization(AccessPolicies.View);

        group.MapGet("", async (string? from, string? to, int? areaId, string? levels, string? rules, int? top, HttpContext http, AlarmAnalyticsService analytics, CancellationToken ct) =>
        {
            var f = await FilterAsync(http, from, to, areaId, levels, rules);
            if (f.Error is not null) return f.Error;
            var th = (await analytics.GetThresholdsAsync(ct)).Thresholds;
            if (top is int n) th.TopN = Math.Clamp(n, 1, 100);
            return Results.Ok(await analytics.AnalyzeAsync(f.From, f.To, f.Include, ScadaTime.NowMs(), th, ct));
        });

        group.MapGet("/trend", async (string? from, string? to, int? areaId, string? levels, string? rules, HttpContext http, AlarmAnalyticsService analytics, CancellationToken ct) =>
        {
            var f = await FilterAsync(http, from, to, areaId, levels, rules);
            if (f.Error is not null) return f.Error;
            var (granularity, points) = await analytics.TrendAsync(f.From, f.To, f.Include, ScadaTime.NowMs(), ct);
            return Results.Ok(new { granularity, points });
        });

        group.MapGet("/events", async (string? from, string? to, int? areaId, string? levels, string? rules, string? source, string? kind, string? ids, int? limit,
            HttpContext http, AlarmAnalyticsService analytics, CancellationToken ct) =>
        {
            var f = await FilterAsync(http, from, to, areaId, levels, rules);
            if (f.Error is not null) return f.Error;
            var idSet = Set(ids);
            if (idSet is { Count: > 2000 }) return EndpointHelpers.BadRequest("一次最多查询 2000 条报警");
            var page = await analytics.EventsAsync(f.From, f.To, f.Include, source, kind, idSet, limit ?? 500, ct);
            return Results.Ok(new { total = page.Total, items = page.Items.Select(x => new { x.Id, x.RuleId, x.TagId, x.DeviceId, x.GatewayId, x.Level, x.StartTs, x.EndTs, x.AckTs, x.AckUser, x.Shelved, x.Suppressed, x.Chattering, x.Message, x.Source }) });
        });

        group.MapGet("/export", async (string? format, string? from, string? to, int? areaId, string? levels, string? rules, int? top,
            HttpContext http, AlarmAnalyticsService analytics, IOptions<ScadaSettings> options, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            var pdf = string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase);
            if (!pdf && !string.Equals(format ?? "xlsx", "xlsx", StringComparison.OrdinalIgnoreCase)) return EndpointHelpers.BadRequest("格式须为 xlsx 或 pdf");
            var f = await FilterAsync(http, from, to, areaId, levels, rules);
            if (f.Error is not null) return f.Error;
            var th = (await analytics.GetThresholdsAsync(ct)).Thresholds;
            if (top is int n) th.TopN = Math.Clamp(n, 1, 100);
            var report = await analytics.AnalyzeAsync(f.From, f.To, f.Include, ScadaTime.NowMs(), th, ct);
            var locale = Localizer.RequestLocale(http);
            var title = Localizer.T("报警统计分析", locale);
            var sections = AlarmAnalyticsExport.Sections(report, analytics.Zone, locale);
            var stamp = ReportExcel.Local(ScadaTime.NowMs(), analytics.Zone);
            var bytes = pdf ? AlarmAnalyticsExport.Pdf(title, sections, options.Value.ReportFontPath, Localizer.T($"生成于 {stamp}", locale)) : AlarmAnalyticsExport.Xlsx(title, sections);
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                db.OperationLogs.Add(new OperationLog
                {
                    UserId = http.User.GetUserId(), UserName = http.User.GetUserName(), Ts = ScadaTime.NowMs(), Action = "导出报警统计分析", Result = "成功", TargetType = "AlarmAnalytics",
                    Details = System.Text.Json.JsonSerializer.Serialize(new { format = pdf ? "pdf" : "xlsx", from = f.From, to = f.To, areaId, levels, rules })
                });
                await db.SaveChangesAsync(ct);
            }
            var name = $"alarm-analytics-{DateTimeOffset.FromUnixTimeMilliseconds(f.From):yyyyMMdd}-{DateTimeOffset.FromUnixTimeMilliseconds(f.To):yyyyMMdd}";
            return pdf ? Results.File(bytes, "application/pdf", name + ".pdf")
                : Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        });

        group.MapGet("/thresholds", async (AlarmAnalyticsService analytics, CancellationToken ct) =>
        {
            var (thresholds, revision) = await analytics.GetThresholdsAsync(ct);
            return Results.Ok(new { thresholds, revision, defaults = analytics.Defaults() });
        });

        group.MapPut("/thresholds", async (AlarmThresholdsRequest request, HttpContext http, AlarmAnalyticsService analytics, AlarmRollupService rollup,
            IOptions<ScadaSettings> options, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (request.Thresholds is null) return EndpointHelpers.BadRequest("阈值不能为空");
            if (request.Thresholds.Validate() is string error) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.AlarmAnalyticsConfigs.SingleOrDefaultAsync(x => x.Id == 1, ct);
            if ((row?.Revision ?? 0) != request.Revision) return Results.Conflict(new { message = "阈值已被他人修改，请刷新后重试" });
            var before = analytics.Parse(row?.ThresholdsJson);
            if (row is null) { row = new AlarmAnalyticsConfig { Id = 1, Revision = 0 }; db.AlarmAnalyticsConfigs.Add(row); }
            row.ThresholdsJson = AlarmAnalyticsService.Serialize(request.Thresholds);
            row.Revision++; row.UpdatedTs = ScadaTime.NowMs(); row.UpdatedBy = http.User.GetUserName();
            db.OperationLogs.Add(Audit.Change(http, "修改报警分析阈值", "AlarmAnalytics", 1, before, request.Thresholds));
            await db.SaveChangesAsync(ct);
            // 瞬时报警秒数影响预聚合中的瞬时计数：重算小时保留期内的预聚合。
            if (before.FleetingSec != request.Thresholds.FleetingSec)
                await rollup.RequestRebuildAsync(ScadaTime.NowMs() - options.Value.AlarmRollupHourlyRetentionDays * AlarmAnalyticsService.DayMs, ct);
            return Results.Ok(new { thresholds = request.Thresholds, revision = row.Revision });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure).AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("/rollups", async (IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var state = await db.AlarmRollupStates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == 1, ct);
            return Results.Ok(new
            {
                watermarkTs = state?.WatermarkTs, lastRunTs = state?.LastRunTs, rebuildFromTs = state?.RebuildFromTs, lastError = state?.LastError,
                hourlyRows = await db.AlarmRollups.CountAsync(x => x.Granularity == AlarmRollup.Hour, ct),
                dailyRows = await db.AlarmRollups.CountAsync(x => x.Granularity == AlarmRollup.Day, ct)
            });
        });

        group.MapPost("/rollups/rebuild", async (AlarmRollupRebuildRequest request, HttpContext http, AlarmRollupService rollup, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (request.Days is < 1 or > 3660) return EndpointHelpers.BadRequest("回补天数须为 1～3660");
            var fromTs = ScadaTime.NowMs() - request.Days * AlarmAnalyticsService.DayMs;
            await rollup.RequestRebuildAsync(fromTs, ct);
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                db.OperationLogs.Add(Audit.Change(http, $"重算报警预聚合：最近 {request.Days} 天", "AlarmAnalytics", 1, null, new { days = request.Days, fromTs }));
                await db.SaveChangesAsync(ct);
            }
            // 立即执行一轮（同一时刻只有一轮在跑）。
            var written = await rollup.RunOnceAsync(ScadaTime.NowMs(), ct);
            return Results.Ok(new { written });
        }).RequireAuthorization(AccessPolicies.GlobalConfigure).AddEndpointFilter<AuditFailuresFilter>();
    }
}
