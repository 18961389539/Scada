using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Scada.Server.Auth;

namespace Scada.Server.Tests;

/// <summary>
/// 传输安全与流量治理的端到端回归。复用 <see cref="AuthorizationTests.Site"/> 的宿主，
/// 因为它已经建好区域受限的 opA 账号——正好用来验证「收紧健康检查后普通操作员还能看健康卡片」。
/// </summary>
public sealed class HardeningTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public HardeningTests(AuthorizationTests.Site site) => _site = site;

    [Fact]
    public async Task LiveProbeIsAnonymousAndLeaksNothing()
    {
        using var client = _site.CreateClient();
        foreach (var path in new[] { "/health/live", "/api/health/live" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
            // 存活探针只允许有 status 一个字段：设备清单、磁盘路径、HA 节点一个都不能带出去。
            Assert.Single(body.RootElement.EnumerateObject());
        }
    }

    [Fact]
    public async Task FullHealthSnapshotRequiresLogin()
    {
        using var client = _site.CreateClient();
        foreach (var path in new[] { "/health", "/api/health" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            // 401 也不能顺手把快照塞进响应体。
            Assert.DoesNotContain("\"devices\"", await response.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// 回归锁：完整快照被前端主布局 / 总览 / 实时监控 / 系统页读取，门槛只能抬到「已登录」。
    /// 曾经误设成 Global:System，结果所有非管理员的健康卡片持续报错，而测试都用 admin 所以没暴露。
    /// </summary>
    [Fact]
    public async Task AreaScopedViewerCanStillReadFullHealthSnapshot()
    {
        using var client = await _site.ClientAsync("opA");
        using var response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("devices", out _));
        Assert.True(body.RootElement.TryGetProperty("status", out _));
    }

    [Fact]
    public void FullHealthEndpointsDoNotCarryASystemPolicy()
    {
        var endpoints = _site.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        foreach (var route in new[] { "/health", "/api/health" })
        {
            var endpoint = Assert.Single(endpoints, e =>
                e is RouteEndpoint re && re.RoutePattern.RawText == route);
            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ToList();
            Assert.NotEmpty(authorize);
            Assert.All(authorize, data => Assert.True(string.IsNullOrEmpty(data.Policy)));
        }
    }

    [Fact]
    public void HealthRequireSystemOptionTightensPolicyWhenExplicitlyEnabled()
    {
        // 独立数据目录：不与 IClassFixture 的宿主抢同一组 SQLite 文件。
        var root = Path.Combine(Path.GetTempPath(), "scada-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Development");
                b.UseSetting("Scada:ConfigDbPath", Path.Combine(root, "config.db"));
                b.UseSetting("Scada:RuntimeDbPath", Path.Combine(root, "runtime.db"));
                b.UseSetting("Scada:HistoryRoot", Path.Combine(root, "history"));
                b.UseSetting("Scada:BackupRoot", Path.Combine(root, "backups"));
                b.UseSetting("Scada:DiagnosticsRoot", Path.Combine(root, "diag"));
                b.UseSetting("Scada:ReportsRoot", Path.Combine(root, "reports"));
                b.UseSetting("Scada:SeedDemoData", "false");
                b.UseSetting("Scada:FullBackupIntervalHours", "0");
                b.UseSetting("Jwt:Key", "hardening-tests-signing-key-0123456789abcdef");
                b.UseSetting("Scada:HealthRequireSystem", "true");
            });
            _ = factory.Server;
            var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
            var endpoint = Assert.Single(endpoints, e =>
                e is RouteEndpoint re && re.RoutePattern.RawText == "/health");
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                data => data.Policy == AccessPolicies.GlobalSystem);
        }
        finally
        {
            TestSqlite.ClearPools(root);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task SecurityHeadersAreAppliedToAnonymousAndApiResponses()
    {
        using var client = _site.CreateClient();
        foreach (var path in new[] { "/health/live", "/api/auth/login" })
        {
            using var response = path.EndsWith("/login", StringComparison.Ordinal)
                ? await client.PostAsJsonAsync(path, new { userName = "nobody", password = "wrong" })
                : await client.GetAsync(path);
            var headers = response.Headers;
            Assert.Equal("nosniff", headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("SAMEORIGIN", headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("same-origin", headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("geolocation=()", headers.GetValues("Permissions-Policy").Single());
            var csp = headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("default-src 'self'", csp);
            Assert.Contains("script-src 'self'", csp);
            // 前端产物是外部 ES module，不能给脚本开 unsafe-inline；只有样式需要（Vue / Element Plus inline style）。
            Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
            Assert.Contains("style-src 'self' 'unsafe-inline'", csp);
            Assert.Contains("connect-src 'self' ws: wss:", csp);
            // TestServer 走明文 HTTP：HSTS 只在真实 HTTPS 请求时下发，不能误导排查。
            Assert.False(headers.Contains("Strict-Transport-Security"));
        }
    }
}
