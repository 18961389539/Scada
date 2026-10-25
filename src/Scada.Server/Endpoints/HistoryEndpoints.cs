using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Engine.History;

namespace Scada.Server.Endpoints;

public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/history", async (
            string? tagIds,
            string? from,
            string? to,
            HttpContext http,
            HistoryQueryService queryService,
            CancellationToken cancellationToken) =>
        {
            var ids = EndpointHelpers.ParseIds(tagIds);
            if (ids.Length == 0)
            {
                return EndpointHelpers.BadRequest("请指定 tagIds");
            }

            if (ids.Length > 50)
            {
                return EndpointHelpers.BadRequest("单次最多查询 50 个测点");
            }

            if (await EndpointHelpers.RequireTagsAsync(http, ids, Permission.View, "查询历史") is { } denied) return denied;

            var toMs = EndpointHelpers.ParseTime(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fromMs = EndpointHelpers.ParseTime(from, toMs - (long)TimeSpan.FromHours(1).TotalMilliseconds);
            if (toMs < fromMs)
            {
                return EndpointHelpers.BadRequest("结束时间不能早于开始时间");
            }

            if (toMs - fromMs > (long)TimeSpan.FromDays(366).TotalMilliseconds)
            {
                return EndpointHelpers.BadRequest("查询跨度不能超过一年");
            }

            var result = await queryService.QueryAsync(
                ids, fromMs, toMs, cancellationToken, HistoryDownsampler.DefaultMaxPoints);
            return Results.Ok(new
            {
                resolution = result.Resolution.ToString(),
                downsampled = result.Downsampled,
                pointLimit = result.PointLimit,
                series = result.Series
            });
        });

        // 分析只基于原始明细（质量区间不能被聚合平均掉），因此限制 20 测点 / 24 小时。
        app.MapGet("/api/history/analysis", async (
            string? tagIds,
            string? from,
            string? to,
            HttpContext http,
            IHistoryStore store,
            CancellationToken cancellationToken) =>
        {
            var ids = EndpointHelpers.ParseIds(tagIds);
            if (ids.Length == 0) return EndpointHelpers.BadRequest("请指定 tagIds");
            if (ids.Length > 20) return EndpointHelpers.BadRequest("单次最多分析 20 个测点");
            if (await EndpointHelpers.RequireTagsAsync(http, ids, Permission.View, "查询历史分析") is { } denied) return denied;

            var toMs = EndpointHelpers.ParseTime(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fromMs = EndpointHelpers.ParseTime(from, toMs - (long)TimeSpan.FromHours(1).TotalMilliseconds);
            if (toMs < fromMs) return EndpointHelpers.BadRequest("结束时间不能早于开始时间");
            if (toMs - fromMs > (long)TimeSpan.FromHours(24).TotalMilliseconds) return EndpointHelpers.BadRequest("历史分析区间不能超过 24 小时");

            var raw = await store.QueryRawAsync(ids, fromMs, toMs, cancellationToken);
            var analyzed = HistoryAnalysis.Analyze(raw, fromMs, toMs).ToDictionary(x => x.TagId);
            var tags = ids.Select(id => analyzed.TryGetValue(id, out var item)
                ? item
                : new HistoryTagAnalysis(id, 0, 0, toMs - fromMs, 0, 0, null, null, null, 0, [], false)).ToList();
            return Results.Ok(new HistoryAnalysisResult(fromMs, toMs, nameof(HistoryResolution.Raw), tags));
        });
    }
}
