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

    public static bool IsMobile(HttpContext http) =>
        string.Equals(http.Request.Headers[ClientHeader].FirstOrDefault(), "mobile", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^/api/(commands|screens/\d+/actions/[^/]+|control/requests/[^/]+/approve|control/sequences/\d+/start|recipes/\d+/download|traceability/jobs/\d+/scan)$", RegexOptions.IgnoreCase)]
    public static partial Regex ControlPath();

    public async Task InvokeAsync(HttpContext http, IDbContextFactory<ConfigDbContext> factory, Microsoft.Extensions.Options.IOptionsMonitor<MobileOptions> mobile)
    {
        if (HttpMethods.IsPost(http.Request.Method) && ControlPath().IsMatch(http.Request.Path.Value ?? "")
            && IsMobile(http) && !mobile.CurrentValue.Permits(http.User))
        {
            var result = await Audit.ForbidAsync(http, $"控制请求 {http.Request.Path}", "移动端不允许下发控制（配置 Mobile:AllowControl / Mobile:ControlRoles）");
            await result.ExecuteAsync(http);
            return;
        }
        var station = http.Request.Headers[WorkstationHeader].FirstOrDefault();
        var hasStationId = http.Request.Headers.ContainsKey(WorkstationIdHeader);
        if (HttpMethods.IsPost(http.Request.Method) && ControlPath().IsMatch(http.Request.Path.Value ?? "")
            && (hasStationId || !string.IsNullOrWhiteSpace(station)))
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
