using Microsoft.Extensions.FileProviders;
using Scada.Core.Options;
using Scada.Server.Endpoints;
using Scada.Server.Mcp;
using Serilog;

namespace Scada.Server.Startup;

// 请求管道与端点映射：中间件顺序即语义，调整前先读各中间件注释。
internal static class ScadaApiPipeline
{
    public static void UseScadaApi(this WebApplication app, ExternalRestOptions externalRest, bool jwtKeyIsTemporary)
    {
        if (jwtKeyIsTemporary)
        {
            app.Logger.LogWarning("Jwt:Key 未配置，本次使用临时签名密钥，重启后已签发的令牌全部失效");
        }

        app.UseSerilogRequestLogging();
        // 压缩必须在本地化外层：本地化要读取明文 JSON 再翻译，翻译后的结果再交给压缩。
        app.UseResponseCompression();
        // 英文界面：翻译 /api 响应中的提示文字（异常处理产生的提示也会被翻译）。
        app.UseMiddleware<Scada.Server.Localization.ResponseLocalization>();
        app.UseExceptionHandler();
        app.UseCors("web");
        if (externalRest.CorsOrigins.Length > 0)
            app.UseCors("external");
        // 静态文件只认一个根：WebApplicationBuilder 的 WebRootFileProvider 会同时包含
        // ContentRoot/wwwroot，导致即使指定了 --webroot 也仍命中程序目录下的陈旧副本（wwwroot 优先）。
        // 这里显式构造单一 FileProvider，让 DefaultFiles / StaticFiles / SPA fallback 用同一来源。
        var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        if (!Directory.Exists(webRoot))
        {
            // 发布包必须自带 wwwroot。缺失时不要直接崩溃：双击启动没有控制台可看，
            // 建空目录让 API 与日志先可用，页面 404 时能通过日志定位。
            Directory.CreateDirectory(webRoot);
            app.Logger.LogWarning("静态文件根不存在，已创建空目录 {WebRoot}；发布包缺少 wwwroot 时前端不可用", webRoot);
        }
        var webRootFiles = new PhysicalFileProvider(webRoot);
        var staticFiles = new StaticFileOptions
        {
            FileProvider = webRootFiles,
            OnPrepareResponse = context =>
            {
                var headers = context.Context.Response.Headers;
                var path = context.Context.Request.Path.Value ?? string.Empty;
                // Vite 产出的 assets 文件名带内容哈希，可放心长缓存；入口 html 必须每次协商，才能及时切到新版本。
                headers["Cache-Control"] = path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
                headers["Vary"] = "Accept-Encoding";
            }
        };
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webRootFiles });
        app.UseStaticFiles(staticFiles);
        if (File.Exists(Path.Combine(webRoot, "index.html")))
        {
            // SPA 路由匿名可达：没有命中任何接口的 GET/HEAD 返回 index.html。
            // 不用路由兜底，避免 catch-all 端点拖累 /api 的方法匹配，导致应得 405 的请求变成 404。
            var spa = ((IEndpointRouteBuilder)app).CreateApplicationBuilder();
            spa.UseStaticFiles(staticFiles);
            var spaFiles = spa.Build();
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (context.GetEndpoint() is null && !path.Contains('.') && !context.Request.Path.StartsWithSegments("/api")
                    && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
                {
                    context.Request.Path = "/index.html";
                    await spaFiles(context);
                    return;
                }
                await next();
            });
        }
        app.UseMiddleware<Scada.Server.Ha.HaStandbyMiddleware>();
        app.UseAuthentication();
        // 审计身份：拦截器与手工审计都从这里取操作者，必须排在认证之后、授权之前。
        app.UseMiddleware<Scada.Server.Auth.AuditActorMiddleware>();
        // 未改密会话先拦在授权之前：出厂口令不能靠“用户自觉”换掉。
        app.UseMiddleware<Scada.Server.Auth.MustChangePasswordMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<Scada.Server.Licensing.LicenseGateMiddleware>();
        app.UseMiddleware<Scada.Server.Auth.ClientControlPolicy>();
        // /api 由 SPA 兜底排除在外：未匹配任何接口时补带路径的 404，已知接口的错误方法仍由路由框架返回 405。
        app.Use(async (context, next) =>
        {
            await next();
            if (context.Response.StatusCode == StatusCodes.Status404NotFound && !context.Response.HasStarted
                && context.GetEndpoint() is null && context.Request.Path.StartsWithSegments("/api"))
                await context.Response.WriteAsJsonAsync(new { message = $"接口不存在：{context.Request.Path}" });
        });
        app.UseScadaMcp();
        app.MapScadaMcp();

        // 端点清单见 Endpoints/EndpointRegistration.cs：集中一处便于核对每个领域都挂上了。
        app.MapScadaEndpoints(externalRest.Enabled);
    }
}
