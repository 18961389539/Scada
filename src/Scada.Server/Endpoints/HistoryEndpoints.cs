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

            var result = await queryService.QueryAsync(ids, fromMs, toMs, cancellationToken);
            return Results.Ok(new
            {
                resolution = result.Resolution.ToString(),
                series = result.Series
            });
        });
    }
}
