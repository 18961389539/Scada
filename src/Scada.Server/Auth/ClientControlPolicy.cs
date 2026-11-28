using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Data;

namespace Scada.Server.Auth;

// 客户端控制策略：在进入控制闭环之前，按发起端（工作站、移动端）拒绝下发类请求。
// 工作站名由浏览器在请求头 X-Scada-Workstation 中声明（本机登记），属于“现场纵深防御”：
// 能防止监视大屏/只读工位误操作，但不能替代用户权限（请求头可由客户端伪造，真正的授权仍是区域控制权限）。
// 移动端控制开关（appsettings "Mobile"）：AllowControl=false 时手机端只能监视；ControlRoles 非空时仅这些角色可在手机上控制。
public sealed class MobileOptions
{
    public bool AllowControl { get; set; } = true;
    public string[] ControlRoles { get; set; } = [];

    public bool Permits(System.Security.Claims.ClaimsPrincipal user) =>
        AllowControl && (ControlRoles.Length == 0 || ControlRoles.Any(r => user.IsInRole(r)));
}

public sealed partial class ClientControlPolicy(RequestDelegate next)
{
    public const string WorkstationHeader = "X-Scada-Workstation";
    public const string WorkstationIdHeader = "X-Scada-Workstation-Id";
    public const string ClientHeader = "X-Scada-Client";
    public const string MonitorOnlyHeader = "X-Scada-Monitor-Only";

    public static bool IsMobile(HttpContext http) =>
        string.Equals(http.Request.Headers[ClientHeader].FirstOrDefault(), "mobile", StringComparison.OrdinalIgnoreCase);

    public static bool IsMonitorOnly(HttpContext http) =>
        string.Equals(http.Request.Headers[MonitorOnlyHeader].FirstOrDefault(), "1", StringComparison.Ordinal);

    // 会改变现场的写路径：写值、画面动作、审批/驳回、顺控启停、配方下发/中止、挂牌、就地/远方、光字牌试验/消音、单条报警确认/备注、正式停机确认/产量修正、质量检验与工单写入、追溯扫码、采集重连、设备连通测试与未保存设备试连、仿真强制/故障/复位、已发布脚本调用。
    // 就地/远方是 PUT /api/control/modes/{id}；清除仿真故障是 DELETE /api/simulation/{id}/fault。因此策略匹配 POST、PUT 与 DELETE。
    [GeneratedRegex(@"^/api/(commands|screens/\d+/actions/[^/]+|control/requests/[^/]+/(approve|reject)|control/sequences/\d+/start|control/runs/\d+/abort|control/lockouts|control/lockouts/\d+/remove|control/modes/\d+|recipes/\d+/download|recipes/downloads/\d+/abort|annunciators/\d+/(silence|clear-silence|test)|annunciators/(silence-visible|ack-visible)|alarm-rules/\d+/(shelve|unshelve|out-of-service)|traceability/jobs/\d+/scan|devices/\d+/diagnostics/reconnect|devices/\d+/test|devices/test-draft|simulation/\d+/(force|release|fault|reset)|calc-jobs/\d+/(invoke|reset|maintenance-done|acknowledge-writes)|alarms/\d+/(ack|notes)|oee/downtimes|oee/downtimes/\d+|oee/downtimes/\d+/confirm|oee/corrections|quality/inspections|quality/inspections/\d+|quality/inspections/\d+/delete|work-orders|work-orders/\d+|work-orders/\d+/status|work-orders/\d+/parts)$", RegexOptions.IgnoreCase)]
    public static partial Regex ControlPath();

    public static bool IsControlRequest(HttpContext http)
    {
        var method = http.Request.Method;
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) && !HttpMethods.IsDelete(method)) return false;
        return ControlPath().IsMatch(http.Request.Path.Value ?? "");
    }

    public async Task InvokeAsync(HttpContext http, IDbContextFactory<ConfigDbContext> factory, Microsoft.Extensions.Options.IOptionsMonitor<MobileOptions> mobile)
    {
        if (IsControlRequest(http) && IsMobile(http) && !mobile.CurrentValue.Permits(http.User))
        {
            var result = await Audit.ForbidAsync(http, $"控制请求 {http.Request.Path}", "移动端不允许下发控制（配置 Mobile:AllowControl / Mobile:ControlRoles）");
            await result.ExecuteAsync(http);
            return;
        }
        if (IsControlRequest(http) && IsMonitorOnly(http))
        {
            var result = await Audit.ForbidAsync(http, $"控制请求 {http.Request.Path}", "本客户端处于监视模式，不能下发控制");
            await result.ExecuteAsync(http);
            return;
        }
        var station = http.Request.Headers[WorkstationHeader].FirstOrDefault();
        var hasStationId = http.Request.Headers.ContainsKey(WorkstationIdHeader);
        if (IsControlRequest(http) && (hasStationId || !string.IsNullOrWhiteSpace(station)))
        {
            await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
            var rawId = http.Request.Headers[WorkstationIdHeader].ToString();
            var row = hasStationId
                ? int.TryParse(rawId, out var stationId) && stationId > 0
                    ? await db.HmiWorkstations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == stationId, http.RequestAborted)
                    : null
                : await db.HmiWorkstations.AsNoTracking().SingleOrDefaultAsync(x => x.Name == station, http.RequestAborted);
            if (row is null)
            {
                var result = await Audit.ForbidAsync(http, $"控制请求 {http.Request.Path}", "工作站未登记或已删除，请重新绑定本机工作站");
                await result.ExecuteAsync(http);
                return;
            }
            if (row is { AllowControl: false })
            {
                var result = await Audit.ForbidAsync(http, $"控制请求 {http.Request.Path}", $"工作站 {row.Name} 只允许监视，不能下发控制");
                await result.ExecuteAsync(http);
                return;
            }
        }
        await next(http);
    }
}
