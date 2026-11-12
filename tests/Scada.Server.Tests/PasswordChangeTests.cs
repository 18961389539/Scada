using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Scada.Server.Tests;

/// <summary>
/// 出厂口令策略：首装 admin/admin123 登录后，除查自己与改密外一律 428；
/// 改密成功立刻换发新令牌，旧令牌因 CredentialsVersion 变化而失效。
/// </summary>
public sealed class PasswordChangeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scada-pwd-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Scada:ConfigDbPath", Path.Combine(_root, "config.db"));
            builder.UseSetting("Scada:HistoryRoot", Path.Combine(_root, "history"));
            builder.UseSetting("Scada:BackupRoot", Path.Combine(_root, "backups"));
            builder.UseSetting("Scada:DiagnosticsRoot", Path.Combine(_root, "diag"));
            builder.UseSetting("Scada:ReportsRoot", Path.Combine(_root, "reports"));
            builder.UseSetting("Scada:ReportSchedulerEnabled", "false");
            builder.UseSetting("Scada:SeedDemoData", "false");
            builder.UseSetting("Scada:FullBackupIntervalHours", "0");
            builder.UseSetting("Jwt:Key", "password-change-tests-signing-key-0123456789abc");
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        TestSqlite.ClearPools(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> LoginAsync(HttpClient client, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password });
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private HttpClient Authorized(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Seed_Admin_Must_Change_Password_Before_Using_Anything_Else()
    {
        using var login = Client();
        var session = await LoginAsync(login, "admin123");
        Assert.True(session.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var token = session.GetProperty("token").GetString()!;

        using var client = Authorized(token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        var blocked = await client.GetAsync("/api/tags");
        Assert.Equal(HttpStatusCode.PreconditionRequired, blocked.StatusCode);
        Assert.Contains("修改密码", await blocked.Content.ReadAsStringAsync());
        // 配置类与命令类同样被拦住，不是只挡某一个前缀。
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.PostAsJsonAsync("/api/commands", new { tagId = 1, value = 1 })).StatusCode);
    }

    [Fact]
    public async Task Change_Password_Issues_A_New_Token_And_Unlocks_The_Session()
    {
        using var login = Client();
        var session = await LoginAsync(login, "admin123");
        var token = session.GetProperty("token").GetString()!;

        using var client = Authorized(token);
        using var wrong = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = "nope", newPassword = "brand-new-1" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        using var same = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = "admin123", newPassword = "admin123" });
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);

        using var changed = await client.PostAsJsonAsync("/api/auth/password", new { currentPassword = "admin123", newPassword = "brand-new-1" });
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        var payload = await changed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(payload.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var fresh = payload.GetProperty("token").GetString()!;
        Assert.NotEqual(token, fresh);

        using var unlocked = Authorized(fresh);
        Assert.Equal(HttpStatusCode.OK, (await unlocked.GetAsync("/api/tags")).StatusCode);
        // 旧令牌随着口令变化立即失效，不会留下一个仍然可用的会话。
        using var stale = Authorized(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/api/tags")).StatusCode);

        // 新口令可以登录，且不再进入强制改密。
        using var relogin = Client();
        var again = await LoginAsync(relogin, "brand-new-1");
        Assert.False(again.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
    }
}
