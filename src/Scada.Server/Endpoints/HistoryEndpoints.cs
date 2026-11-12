using Microsoft.EntityFrameworkCore;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.History;
using Scada.Server.Auth;
using HistoryValue = Scada.Core.Models.HistoryValue;

namespace Scada.Server.Endpoints;

public sealed record HistorySnapshotItem(int TagId, double? Value, TagQuality? Quality, long? SourceTs, string? Error, string? Text = null);

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

        // 历史复盘：返回指定时刻之前每个测点的最后一条原始采样，用于还原整幅运行画面。
        app.MapGet("/api/history/snapshot", async (
            string? tagIds,
            long? ts,
            HttpContext http,
            HistoryQueryService queryService,
            IDbContextFactory<ConfigDbContext> factory,
            CancellationToken cancellationToken) =>
        {
            if (ts is not long at || at <= 0) return EndpointHelpers.BadRequest("请指定复盘时刻 ts（毫秒时间戳）");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (at > now + 60000) return EndpointHelpers.BadRequest("复盘时刻不能晚于当前时间");

            var requested = EndpointHelpers.ParseIds(tagIds);
            if (requested.Length > 300) return EndpointHelpers.BadRequest("单次最多还原 300 个测点");
            var access = await http.GetAccessAsync();
            var visible = requested.Where(id => access.CanTag(Permission.View, id)).ToArray();
            if (visible.Length == 0) return Results.Ok(new { ts = at, found = 0, items = Array.Empty<HistorySnapshotItem>() });

            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var tags = await db.Tags.AsNoTracking()
                .Where(x => visible.Contains(x.Id))
                .Select(x => new { x.Id, x.ArchiveMode })
                .ToListAsync(cancellationToken);
            var archived = tags.Where(x => x.ArchiveMode != ArchiveMode.None).Select(x => x.Id).ToArray();
            var rows = archived.Length == 0
                ? Array.Empty<HistoryValue>()
                : (await queryService.SnapshotAsync(archived, at, cancellationToken)).ToArray();
            var values = rows.ToDictionary(x => x.TagId);
            var items = tags.Select(tag =>
            {
                if (tag.ArchiveMode == ArchiveMode.None) return new HistorySnapshotItem(tag.Id, null, null, null, "未配置历史存储");
                var row = values.GetValueOrDefault(tag.Id);
                return row is null
                    ? new HistorySnapshotItem(tag.Id, null, null, null, "该时刻之前没有历史数据")
                    : new HistorySnapshotItem(tag.Id, row.Value, row.Quality, row.Ts, null, row.Text);
            }).OrderBy(x => x.TagId).ToArray();
            return Results.Ok(new { ts = at, found = values.Count, items });
        }).WithSummary("Restore the last historical sample of each tag before a timestamp");
        app.MapGet("/api/history/analysis", async (
            string? tagIds,
            string? from,
            string? to,
            HttpContext http,
            HistoryQueryService queryService,
            IDbContextFactory<ConfigDbContext> factory,
            CancellationToken cancellationToken) =>
        {
            var ids = EndpointHelpers.ParseIds(tagIds);
            if (ids.Length == 0) return EndpointHelpers.BadRequest("请指定 tagIds");
            if (ids.Length > 20) return EndpointHelpers.BadRequest("单次最多分析 20 个测点");
            if (await EndpointHelpers.RequireTagsAsync(http, ids, Permission.View, "查询历史分析") is { } denied) return denied;
            await using (var db = await factory.CreateDbContextAsync(cancellationToken))
            {
                if (await db.Tags.AsNoTracking().AnyAsync(x => ids.Contains(x.Id) && x.DataType == TagDataType.String, cancellationToken))
                    return EndpointHelpers.BadRequest("字符串测点不支持历史分析");
            }

            var toMs = EndpointHelpers.ParseTime(to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var fromMs = EndpointHelpers.ParseTime(from, toMs - (long)TimeSpan.FromHours(1).TotalMilliseconds);
            if (toMs < fromMs) return EndpointHelpers.BadRequest("结束时间不能早于开始时间");
            if (toMs - fromMs > (long)TimeSpan.FromHours(24).TotalMilliseconds) return EndpointHelpers.BadRequest("历史分析区间不能超过 24 小时");

            var raw = await queryService.QueryRawExactAsync(ids, fromMs, toMs, cancellationToken);
            var analyzed = HistoryAnalysis.Analyze(raw, fromMs, toMs).ToDictionary(x => x.TagId);
            var tags = ids.Select(id => analyzed.TryGetValue(id, out var item)
                ? item
                : new HistoryTagAnalysis(id, 0, 0, toMs - fromMs, 0, 0, null, null, null, 0, [], false)).ToList();
            return Results.Ok(new HistoryAnalysisResult(fromMs, toMs, nameof(HistoryResolution.Raw), tags));
        });
    }
}
