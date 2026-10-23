using Scada.Core.Interfaces;

namespace Scada.Server.Endpoints;

public static class RealtimeEndpoints
{
    public static void MapRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/realtime", (
            string? tagIds,
            IRealtimeCache cache) =>
        {
            var ids = EndpointHelpers.ParseIds(tagIds);
            var values = ids.Length == 0
                ? cache.Snapshot().Values.OrderBy(x => x.TagId).ToList()
                : cache.GetMany(ids);
            return Results.Ok(values);
        });
    }
}
