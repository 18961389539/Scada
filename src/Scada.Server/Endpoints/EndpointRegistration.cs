using Scada.Server.Ai;
using Scada.Server.Auth;
using Scada.Server.External.Rest;
using Scada.Server.Licensing;

namespace Scada.Server.Endpoints;

// 端点注册清单：集中在一处，便于核对"每个领域都挂上了"，也让 Program.cs 只剩管道与启动逻辑。
// 顺序不影响路由匹配（最小 API 按模板与方法的特异性选择），保持原来的书写顺序便于对照。
public static class EndpointRegistration
{
    public static void MapScadaEndpoints(this IEndpointRouteBuilder app, bool externalApiEnabled)
    {
        app.MapLicenseEndpoints();
        app.MapAuthEndpoints();
        app.MapDeviceEndpoints();
        app.MapGatewayEndpoints();
        app.MapCollectorDiagnosticEndpoints();
        app.MapOpcEndpoints();
        app.MapTagEndpoints();
        app.MapCalculatedTagEndpoints();
        app.MapCalcJobEndpoints();
        app.MapReportTemplateEndpoints();
        app.MapProjectEndpoints();
        app.MapAlarmRuleEndpoints();
        app.MapRealtimeEndpoints();
        app.MapHistoryEndpoints();
        app.MapAlarmEndpoints();
        app.MapAlarmAnalyticsEndpoints();
        app.MapAnnunciatorEndpoints();
        app.MapOeeEndpoints();
        app.MapAlarmShelveEndpoints();
        app.MapNotificationEndpoints();
        app.MapCommandEndpoints();
        app.MapControlEndpoints();
        app.MapRecipeEndpoints();
        app.MapTraceabilityEndpoints();
        app.MapReportEndpoints();
        app.MapLogEndpoints();
        app.MapUserEndpoints();
        app.MapAccessEndpoints();
        app.MapScreenEndpoints();
        app.MapHmiLibraryEndpoints();
        app.MapHmiLayoutEndpoints();

        // 客户端策略：前端据此在手机上隐藏/禁用控制入口（服务端中间件仍会强制拒绝）。
        app.MapGet("/api/client-policy", (HttpContext http, Microsoft.Extensions.Options.IOptionsMonitor<MobileOptions> mobile) =>
            Results.Ok(new { mobileControl = mobile.CurrentValue.Permits(http.User) })).RequireAuthorization();

        app.MapObjectTemplateEndpoints();
        app.MapExternalRestEndpoints();
        app.MapExternalAdminEndpoints();
        app.MapAiEndpoints();
        if (externalApiEnabled)
        {
            app.MapOpenApi("/openapi/v1.json").AllowAnonymous();
            // AI 只读契约单独一份文档，方便直接把工具定义喂给模型。
            app.MapOpenApi("/openapi/ai.json").AllowAnonymous();
        }

        app.MapHealthEndpoints();
        app.MapMetricsEndpoints();
        app.MapMaintenanceEndpoints();
        Ha.HaEndpoints.MapHaEndpoints(app);
        app.MapHub<Hubs.RealtimeHub>("/hubs/realtime");
    }
}
