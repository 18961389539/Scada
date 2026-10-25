using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Scada.Data;

namespace Scada.Server.Auth;

// 客户端控制策略：在进入控制闭环之前，按发起端（工作站、移动端）拒绝下发类请求。
// 工作站名由浏览器在请求头 X-Scada-Workstation 中声明（本机登记），属于“现场纵深防御”：
// 能防止监视大屏/只读工位误操作，但不能替代用户权限（请求头可由客户端伪造，真正的授权仍是区域控制权限）。
public sealed partial class ClientControlPolicy(RequestDelegate next)
{
    public const string WorkstationHeader = "X-Scada-Workstation";

    [GeneratedRegex(@"^/api/(commands|screens/\d+/actions/[^/]+|control/requests/[^/]+/approve|control/sequences/\d+/start|recipes/\d+/download)$", RegexOptions.IgnoreCase)]
    public static partial Regex ControlPath();

    public async Task InvokeAsync(HttpContext http, IDbContextFactory<ConfigDbContext> factory)
    {
        if (HttpMethods.IsPost(http.Request.Method) && ControlPath().IsMatch(http.Request.Path.Value ?? "")
            && http.Request.Headers[WorkstationHeader].FirstOrDefault() is { Length: > 0 } station)
        {
            await using var db = await factory.CreateDbContextAsync(http.RequestAborted);
            var row = await db.HmiWorkstations.AsNoTracking().SingleOrDefaultAsync(x => x.Name == station, http.RequestAborted);
            if (row is { AllowControl: false })
            {
                var result = await Audit.ForbidAsync(http, $"控制请求 {http.Request.Path}", $"工作站 {station} 只允许监视，不能下发控制");
                await result.ExecuteAsync(http);
                return;
            }
        }
        await next(http);
    }
}
