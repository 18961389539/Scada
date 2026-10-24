using Scada.Core.Interfaces;
using Scada.Core.Security;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public static class RealtimeEndpoints
{
    public static void MapRealtimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/realtime", async (
            string? tagIds,
            HttpContext http,
            IRealtimeCache cache) =>
        {
            var access = await http.GetAccessAsync();
            var ids = EndpointHelpers.ParseIds(tagIds);
            var values = ids.Length == 0
                ? cache.Snapshot().Values.OrderBy(x => x.TagId).ToList()
                : cache.GetMany(ids);
            // 无权区域的测点直接过滤，不返回也不报错，避免泄露存在性。
            return Results.Ok(values.Where(x => access.CanTag(Permission.View, x.TagId)).ToList());
        });
    }
}
