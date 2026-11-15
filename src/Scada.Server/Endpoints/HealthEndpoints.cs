using Scada.Core.Options;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

/// <summary>
/// 健康检查分三层，混在一起是常见错误：
/// <list type="bullet">
///   <item><c>/health/live</c>：存活探针。只回答"进程还在跑"，恒匿名、无 IO、无现场信息。给负载均衡与看门狗用。</item>
///   <item><c>/health</c>、<c>/api/health</c>：完整快照。含设备清单、测点数量、磁盘路径与主备节点，
///   默认只要求登录；此前是 <c>AllowAnonymous</c>，等于把现场拓扑给任何能访问端口的人。
///   前端主布局、总览、实时监控、系统页都读它，因此不能只放行系统管理员；
///   需要更严的现场可设 <c>Scada:HealthRequireSystem=true</c>。</item>
///   <item><c>/api/v1/health</c>：对外集成契约，鉴权在 <see cref="External.Rest.ExternalRestEndpoints"/>，与这里无关。</item>
/// </list>
/// 外部监控（Zabbix、blackbox_exporter）需要匿名读取时设 <c>Scada:HealthAnonymous=true</c>，
/// 代价是上述信息重新对未登录者可见——务必只在可信网段开启。
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app, ScadaSettings settings)
    {
        app.MapGet("/health/live", Live).AllowAnonymous();
        app.MapGet("/api/health/live", Live).AllowAnonymous();

        foreach (var route in new[] { "/health", "/api/health" })
        {
            var endpoint = app.MapGet(route, Handle);
            if (settings.HealthAnonymous) endpoint.AllowAnonymous();
            else if (settings.HealthRequireSystem) endpoint.RequireAuthorization(AccessPolicies.GlobalSystem);
            else endpoint.RequireAuthorization();
        }
    }

    // 存活探针不读磁盘、不查库、不碰采集器：任何一次 IO 都可能把"进程活着但盘挂了"误报成"进程已死"。
    private static IResult Live() => Results.Ok(new { status = "ok" });

    private static IResult Handle(HealthSnapshotService health) => Results.Ok(health.Snapshot());
}
