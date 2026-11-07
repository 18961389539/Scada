using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record SpcChartRequest(
    string Name, bool Enabled, int AreaId, int TagId, SpcChartType ChartType, int SubgroupSize,
    int SamplingIntervalSec, int BaselineSubgroups, bool AutoLimits,
    double? CenterLine, double? UpperControlLimit, double? LowerControlLimit,
    double? SpecUpperLimit, double? SpecLowerLimit,
    bool RuleBeyond3Sigma, bool RuleTwoOfThreeBeyond2Sigma, bool RuleFourOfFiveBeyond1Sigma,
    bool RuleEightOnOneSide, bool RuleRangeBeyondLimit, string? Description, int Revision = 0);

public sealed record SpcSampleView(
    long Id, long Ts, double Mean, double Range, int SubgroupSize, string ValuesJson,
    double? CenterLine, double? UpperControlLimit, double? LowerControlLimit,
    double? RangeCenter, double? RangeUpperControlLimit, double? RangeLowerControlLimit,
    string RuleCodes, double? Cp, double? Cpk);

public sealed record SpcChartView(
    int Id, string Name, bool Enabled, int AreaId, int TagId, string TagName, string? Unit,
    string ChartType, int SubgroupSize, int SamplingIntervalSec, int BaselineSubgroups,
    bool AutoLimits, double? CenterLine, double? UpperControlLimit, double? LowerControlLimit,
    double? RangeCenter, double? RangeUpperControlLimit, double? RangeLowerControlLimit,
    long? LimitsEstablishedTs, bool LimitsProvisional, double? SpecUpperLimit, double? SpecLowerLimit,
    bool RuleBeyond3Sigma, bool RuleTwoOfThreeBeyond2Sigma, bool RuleFourOfFiveBeyond1Sigma,
    bool RuleEightOnOneSide, bool RuleRangeBeyondLimit, string Description, int Revision,
    SpcSampleView? LatestSample, IReadOnlyList<string> ActiveRules);

public sealed record SpcViolationView(
    long Id, int ChartId, long SampleId, string Rule, long Ts, int? AlarmEventId, long? RecoveredTs, string Message);

public static class SpcEndpoints
{
    private static readonly JsonSerializerOptions CopyOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void MapSpcEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spc").AddEndpointFilter<AuditFailuresFilter>().RequireAuthorization(AccessPolicies.View);

        group.MapGet("/charts", async (
            HttpContext http, IDbContextFactory<ConfigDbContext> factory,
            IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var charts = await db.SpcCharts.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
            charts = charts.Where(x => access.Has(Permission.View, x.AreaId) && access.CanTag(Permission.View, x.TagId)).ToList();
            if (charts.Count == 0) return Results.Ok(Array.Empty<SpcChartView>());
            var tags = await db.Tags.AsNoTracking().Where(x => charts.Select(c => c.TagId).Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var ids = charts.Select(x => x.Id).ToArray();
            var active = await runtime.SpcViolations.AsNoTracking()
                .Where(x => ids.Contains(x.ChartId) && x.RecoveredTs == null).ToListAsync(ct);
            var latestTimes = await runtime.SpcSamples.AsNoTracking()
                .Where(x => ids.Contains(x.ChartId))
                .GroupBy(x => x.ChartId)
                .Select(g => new { ChartId = g.Key, Ts = g.Max(x => x.Ts) })
                .ToListAsync(ct);
            var tsSet = latestTimes.Select(x => x.Ts).Distinct().ToArray();
            var candidates = tsSet.Length == 0
                ? new List<SpcSample>()
                : await runtime.SpcSamples.AsNoTracking()
                    .Where(x => ids.Contains(x.ChartId) && tsSet.Contains(x.Ts)).ToListAsync(ct);
            var views = charts.Select(chart =>
            {
                var latest = candidates
                    .Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision)
                    .OrderByDescending(x => x.Ts).FirstOrDefault();
                return View(chart, tags.GetValueOrDefault(chart.TagId), latest,
                    active.Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision));
            }).ToList();
            return Results.Ok(views);
        });

        group.MapGet("/charts/{id:int}", async (
            int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory,
            IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var chart = await db.SpcCharts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (chart is null) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (!access.Has(Permission.View, chart.AreaId) || !access.CanTag(Permission.View, chart.TagId)) return Results.NotFound();
            var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == chart.TagId, ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var latest = await runtime.SpcSamples.AsNoTracking()
                .Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision)
                .OrderByDescending(x => x.Ts).FirstOrDefaultAsync(ct);
            var active = await runtime.SpcViolations.AsNoTracking()
                .Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision && x.RecoveredTs == null).ToListAsync(ct);
            return Results.Ok(View(chart, tag, latest, active));
        });

        group.MapGet("/charts/{id:int}/samples", async (
            int id, long? from, long? to, int? take, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var chart = await db.SpcCharts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (chart is null) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (!access.Has(Permission.View, chart.AreaId) || !access.CanTag(Permission.View, chart.TagId)) return Results.NotFound();
            var now = ScadaTime.NowMs();
            var start = from ?? now - 24 * 3600_000L;
            var end = to ?? now;
            if (start >= end) return EndpointHelpers.BadRequest("时间范围无效");
            if (end - start > 30L * 24 * 3600_000) return EndpointHelpers.BadRequest("时间范围不能超过 30 天");
            var limit = Math.Clamp(take ?? 800, 1, 2000);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var rows = await runtime.SpcSamples.AsNoTracking()
                .Where(x => x.ChartId == id && x.Revision == chart.Revision && x.Ts >= start && x.Ts <= end)
                .OrderByDescending(x => x.Ts).Take(limit).ToListAsync(ct);
            rows.Reverse();
            return Results.Ok(rows.Select(SampleView).ToList());
        });

        group.MapGet("/charts/{id:int}/violations", async (
            int id, long? from, long? to, bool? activeOnly, int? take, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var chart = await db.SpcCharts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (chart is null) return Results.NotFound();
            var access = await http.GetAccessAsync();
            if (!access.Has(Permission.View, chart.AreaId) || !access.CanTag(Permission.View, chart.TagId)) return Results.NotFound();
            var now = ScadaTime.NowMs();
            var start = from ?? now - 7L * 24 * 3600_000;
            var end = to ?? now;
            if (start >= end) return EndpointHelpers.BadRequest("时间范围无效");
            var limit = Math.Clamp(take ?? 200, 1, 500);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var query = runtime.SpcViolations.AsNoTracking()
                .Where(x => x.ChartId == id && x.Revision == chart.Revision && x.Ts >= start && x.Ts <= end);
            if (activeOnly is true) query = query.Where(x => x.RecoveredTs == null);
            var rows = await query.OrderByDescending(x => x.Ts).Take(limit).ToListAsync(ct);
            return Results.Ok(rows.Select(x => new SpcViolationView(
                x.Id, x.ChartId, x.SampleId, x.Rule.ToString(), x.Ts, x.AlarmEventId, x.RecoveredTs, x.Message)).ToList());
        });

        group.MapPost("/charts", async (SpcChartRequest request, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (await EndpointHelpers.RequireAreaAsync(http, request.AreaId, Permission.Configure, $"创建 SPC 控制图：{request.Name}") is { } denied)
                return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.TagId, ct);
            if (tag is null) return EndpointHelpers.BadRequest("测点不存在");
            if (tag.DataType == TagDataType.Bool) return EndpointHelpers.BadRequest("布尔测点不能用于 SPC");
            var access = await http.GetAccessAsync();
            if (!access.CanTag(Permission.Configure, tag.Id)) return await Audit.ForbidAsync(http, "创建 SPC 控制图", "无权配置该测点");
            if (!access.Model.AreasOfTag(tag.Id).Contains(request.AreaId)) return EndpointHelpers.BadRequest("测点不属于所选区域");
            var now = ScadaTime.NowMs();
            var chart = new SpcChart { CreatedTs = now, Revision = 1, ConfigVersion = 1 };
            Apply(chart, request, now, http.User.GetUserName());
            var error = SpcValidation.Chart(chart);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            db.SpcCharts.Add(chart);
            await db.SaveChangesAsync(ct);
            db.OperationLogs.Add(Audit.Change(http, $"创建 SPC 控制图：{chart.Name}", "SpcChart", chart.Id, null, chart));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/spc/charts/{chart.Id}", await BuildViewAsync(db, runtimeFactory, chart, ct));
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/charts/{id:int}", async (int id, SpcChartRequest request, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var chart = await db.SpcCharts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (chart is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, chart.AreaId, Permission.Configure, $"修改 SPC 控制图 #{id}") is { } denied)
                return denied;
            if (request.AreaId != chart.AreaId &&
                await EndpointHelpers.RequireAreaAsync(http, request.AreaId, Permission.Configure, $"移动 SPC 控制图 #{id}") is { } deniedMove)
                return deniedMove;
            if (request.Revision > 0 && chart.ConfigVersion != request.Revision) return Results.Conflict(new { message = "控制图已被修改，请刷新后重试" });
            var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.TagId, ct);
            if (tag is null) return EndpointHelpers.BadRequest("测点不存在");
            if (tag.DataType == TagDataType.Bool) return EndpointHelpers.BadRequest("布尔测点不能用于 SPC");
            var access = await http.GetAccessAsync();
            if (!access.CanTag(Permission.Configure, tag.Id)) return await Audit.ForbidAsync(http, "修改 SPC 控制图", "无权配置该测点");
            if (!access.Model.AreasOfTag(tag.Id).Contains(request.AreaId)) return EndpointHelpers.BadRequest("测点不属于所选区域");
            var before = JsonCopy(chart);
            var requestSubgroupSize = request.ChartType == SpcChartType.Individuals ? 1 : request.SubgroupSize;
            // 只有影响采样口径的变化才切换采样代次；改名、说明、启用、规则或规格限变化保留历史样本。
            var samplingChanged = chart.TagId != request.TagId || chart.ChartType != request.ChartType ||
                chart.SubgroupSize != requestSubgroupSize || chart.SamplingIntervalSec != request.SamplingIntervalSec ||
                chart.BaselineSubgroups != request.BaselineSubgroups || chart.AutoLimits != request.AutoLimits;
            chart.ConfigVersion++;
            if (samplingChanged)
            {
                chart.Revision++;
                chart.CenterLine = null; chart.UpperControlLimit = null; chart.LowerControlLimit = null;
                chart.RangeCenter = null; chart.RangeUpperControlLimit = null; chart.RangeLowerControlLimit = null;
                chart.LimitsEstablishedTs = null;
            }
            Apply(chart, request, ScadaTime.NowMs(), http.User.GetUserName());
            var error = SpcValidation.Chart(chart);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            db.OperationLogs.Add(Audit.Change(http, $"修改 SPC 控制图：{chart.Name}", "SpcChart", chart.Id, before, chart));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "控制图已被修改，请刷新后重试" }); }
            return Results.Ok(await BuildViewAsync(db, runtimeFactory, chart, ct));
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/charts/{id:int}/reset-limits", async (int id, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var chart = await db.SpcCharts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (chart is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, chart.AreaId, Permission.Configure, $"重置 SPC 控制限 #{id}") is { } denied)
                return denied;
            var before = JsonCopy(chart);
            chart.Revision++;
            chart.ConfigVersion++;
            chart.CenterLine = null; chart.UpperControlLimit = null; chart.LowerControlLimit = null;
            chart.RangeCenter = null; chart.RangeUpperControlLimit = null; chart.RangeLowerControlLimit = null;
            chart.LimitsEstablishedTs = null;
            chart.UpdatedTs = ScadaTime.NowMs(); chart.UpdatedBy = http.User.GetUserName();
            db.OperationLogs.Add(Audit.Change(http, $"重置 SPC 控制限：{chart.Name}", "SpcChart", chart.Id, before, chart));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "控制图已被修改，请刷新后重试" }); }
            return Results.Ok(await BuildViewAsync(db, runtimeFactory, chart, ct));
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/charts/{id:int}", async (int id, HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var chart = await db.SpcCharts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (chart is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, chart.AreaId, Permission.Configure, $"删除 SPC 控制图 #{id}") is { } denied)
                return denied;
            db.SpcCharts.Remove(chart);
            db.OperationLogs.Add(Audit.Change(http, $"删除 SPC 控制图：{chart.Name}", "SpcChart", chart.Id, chart, null));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);
    }

    private static void Apply(SpcChart chart, SpcChartRequest request, long now, string user)
    {
        chart.Name = request.Name?.Trim() ?? "";
        chart.Enabled = request.Enabled;
        chart.AreaId = request.AreaId;
        chart.TagId = request.TagId;
        chart.ChartType = request.ChartType;
        chart.SubgroupSize = request.ChartType == SpcChartType.Individuals ? 1 : request.SubgroupSize;
        chart.SamplingIntervalSec = request.SamplingIntervalSec;
        chart.BaselineSubgroups = request.BaselineSubgroups;
        chart.AutoLimits = request.AutoLimits;
        if (!request.AutoLimits)
        {
            chart.CenterLine = request.CenterLine;
            chart.UpperControlLimit = request.UpperControlLimit;
            chart.LowerControlLimit = request.LowerControlLimit;
        }
        chart.SpecUpperLimit = request.SpecUpperLimit;
        chart.SpecLowerLimit = request.SpecLowerLimit;
        chart.RuleBeyond3Sigma = request.RuleBeyond3Sigma;
        chart.RuleTwoOfThreeBeyond2Sigma = request.RuleTwoOfThreeBeyond2Sigma;
        chart.RuleFourOfFiveBeyond1Sigma = request.RuleFourOfFiveBeyond1Sigma;
        chart.RuleEightOnOneSide = request.RuleEightOnOneSide;
        chart.RuleRangeBeyondLimit = request.RuleRangeBeyondLimit;
        chart.Description = request.Description?.Trim() ?? "";
        chart.UpdatedTs = now;
        chart.UpdatedBy = user;
    }

    private static async Task<SpcChartView> BuildViewAsync(ConfigDbContext db,
        IDbContextFactory<RuntimeDbContext> runtimeFactory, SpcChart chart, CancellationToken ct)
    {
        var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == chart.TagId, ct);
        await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
        var latest = await runtime.SpcSamples.AsNoTracking()
            .Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision)
            .OrderByDescending(x => x.Ts).FirstOrDefaultAsync(ct);
        var active = await runtime.SpcViolations.AsNoTracking()
            .Where(x => x.ChartId == chart.Id && x.Revision == chart.Revision && x.RecoveredTs == null).ToListAsync(ct);
        return View(chart, tag, latest, active);
    }

    private static SpcChartView View(SpcChart chart, Tag? tag, SpcSample? latest, IEnumerable<SpcViolation> active) =>
        new(chart.Id, chart.Name, chart.Enabled, chart.AreaId, chart.TagId,
            tag?.Name ?? $"#{chart.TagId}", tag?.Unit, chart.ChartType.ToString(), chart.SubgroupSize,
            chart.SamplingIntervalSec, chart.BaselineSubgroups, chart.AutoLimits,
            chart.CenterLine, chart.UpperControlLimit, chart.LowerControlLimit,
            chart.RangeCenter, chart.RangeUpperControlLimit, chart.RangeLowerControlLimit,
            chart.LimitsEstablishedTs, chart.AutoLimits && chart.LimitsEstablishedTs is null,
            chart.SpecUpperLimit, chart.SpecLowerLimit, chart.RuleBeyond3Sigma, chart.RuleTwoOfThreeBeyond2Sigma,
            chart.RuleFourOfFiveBeyond1Sigma, chart.RuleEightOnOneSide, chart.RuleRangeBeyondLimit,
            chart.Description, chart.ConfigVersion, latest is null ? null : SampleView(latest),
            active.OrderBy(x => x.Rule).Select(x => x.Rule.ToString()).Distinct().ToList());

    private static SpcSampleView SampleView(SpcSample sample) =>
        new(sample.Id, sample.Ts, sample.Mean, sample.Range, sample.SubgroupSize, sample.ValuesJson,
            sample.CenterLine, sample.UpperControlLimit, sample.LowerControlLimit,
            sample.RangeCenter, sample.RangeUpperControlLimit, sample.RangeLowerControlLimit,
            sample.RuleCodes, sample.Cp, sample.Cpk);

    private static JsonElement JsonCopy(SpcChart value) => JsonSerializer.SerializeToElement(value, value.GetType(), CopyOptions);
}
