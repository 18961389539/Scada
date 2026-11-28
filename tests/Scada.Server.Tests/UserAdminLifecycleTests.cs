using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Scada.Server.Auth;

namespace Scada.Server.Tests;

/// <summary>
/// 用户管理边界：删除只统计启用中的管理员；只有内置 Admin 才能提权或管理管理员账号。
/// </summary>
public sealed class UserAdminLifecycleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scada-user-admin-" + Guid.NewGuid().ToString("N"));
    private WebApplicationFactory<Program> _factory = null!;
    private int _adminId;
    private int _managerId;
    private int _privilegedRoleId;
    private int _viewRoleId;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Scada:ConfigDbPath", Path.Combine(_root, "config.db"));
            builder.UseSetting("Scada:RuntimeDbPath", Path.Combine(_root, "runtime.db"));
            builder.UseSetting("Scada:HistoryRoot", Path.Combine(_root, "history"));
            builder.UseSetting("Scada:BackupRoot", Path.Combine(_root, "backups"));
            builder.UseSetting("Scada:DiagnosticsRoot", Path.Combine(_root, "diag"));
            builder.UseSetting("Scada:ReportsRoot", Path.Combine(_root, "reports"));
            builder.UseSetting("Scada:ReportSchedulerEnabled", "false");
            builder.UseSetting("Scada:SeedDemoData", "false");
            builder.UseSetting("Scada:RequirePasswordChangeOnFirstLogin", "false");
            builder.UseSetting("Scada:FullBackupIntervalHours", "0");
            builder.UseSetting("Jwt:Key", "user-admin-lifecycle-tests-signing-key-01234567");
        });
        _ = _factory.Server;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        TestSqlite.TryDeleteDirectory(_root);
    }

    private HttpClient Anonymous() => _factory.CreateClient();

    private async Task<HttpClient> ClientAsync(string user, string password)
    {
        using var login = Anonymous();
        var response = await login.PostAsJsonAsync("/api/auth/login", new { userName = user, password });
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task EnsureSeedAsync()
    {
        if (_adminId != 0) return;
        using var admin = await ClientAsync("admin", "admin123");
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me");
        _adminId = me.GetProperty("id").GetInt32();

        _privilegedRoleId = await IdAsync(await admin.PostAsJsonAsync("/api/roles", new { name = "用户管理员", permissions = new[] { "View", "ManageUsers" } }));
        _viewRoleId = await IdAsync(await admin.PostAsJsonAsync("/api/roles", new { name = "只读角色", permissions = new[] { "View" } }));
        _managerId = await IdAsync(await admin.PostAsJsonAsync("/api/users", new { userName = "usermgr", password = "secret123", displayName = "用户管理员", role = "None", enabled = true }));
        var assigned = await admin.PutAsJsonAsync($"/api/users/{_managerId}/assignments", new { assignments = new[] { new { roleId = _privilegedRoleId } } });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
    }

    private static async Task<int> IdAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task AssertBadRequest(HttpResponseMessage response, string message)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Manager_Cannot_Create_Or_Promote_Admin_But_Can_Create_Operator()
    {
        await EnsureSeedAsync();
        using var manager = await ClientAsync("usermgr", "secret123");

        await AssertBadRequest(
            await manager.PostAsJsonAsync("/api/users", new { userName = "new-admin", password = "secret123", displayName = "新管理员", role = "Admin", enabled = true }),
            UserAdministration.DenyPromoteAdmin);

        var created = await manager.PostAsJsonAsync("/api/users", new { userName = "new-operator", password = "secret123", displayName = "操作员", role = "Operator", enabled = true });
        var operatorId = await IdAsync(created);

        await AssertBadRequest(
            await manager.PutAsJsonAsync($"/api/users/{operatorId}", new { displayName = "操作员", role = "Admin", enabled = true }),
            UserAdministration.DenyPromoteAdmin);

        await AssertBadRequest(
            await manager.PutAsJsonAsync($"/api/users/{_managerId}", new { displayName = "用户管理员", role = "Admin", enabled = true }),
            UserAdministration.DenyPromoteAdmin);
    }

    [Fact]
    public async Task Manager_Cannot_Grant_Privileged_Permissions_Or_Manage_Admin_Accounts()
    {
        await EnsureSeedAsync();
        using var manager = await ClientAsync("usermgr", "secret123");

        await AssertBadRequest(
            await manager.PostAsJsonAsync("/api/roles", new { name = "系统维护", permissions = new[] { "View", "System" } }),
            UserAdministration.DenyGrantPrivileged);
        await AssertBadRequest(
            await manager.PostAsJsonAsync("/api/roles", new { name = "再一个用户管理", permissions = new[] { "ManageUsers" } }),
            UserAdministration.DenyGrantPrivileged);

        var viewOnly = await manager.PostAsJsonAsync("/api/roles", new { name = "经理只读", permissions = new[] { "View" } });
        Assert.True(viewOnly.IsSuccessStatusCode, await viewOnly.Content.ReadAsStringAsync());

        await AssertBadRequest(
            await manager.PutAsJsonAsync($"/api/roles/{_viewRoleId}", new { name = "只读角色", permissions = new[] { "View", "System" } }),
            UserAdministration.DenyGrantPrivileged);
        await AssertBadRequest(
            await manager.PutAsJsonAsync($"/api/roles/{_privilegedRoleId}", new { name = "用户管理员", permissions = new[] { "View" } }),
            UserAdministration.DenyGrantPrivileged);

        await AssertBadRequest(
            await manager.PutAsJsonAsync($"/api/users/{_managerId}/assignments", new { assignments = new[] { new { roleId = _privilegedRoleId } } }),
            UserAdministration.DenyGrantPrivileged);

        await AssertBadRequest(
            await manager.PutAsJsonAsync($"/api/users/{_adminId}", new { displayName = "admin", role = "Admin", enabled = true }),
            UserAdministration.DenyManageAdmin);
        await AssertBadRequest(
            await manager.PostAsJsonAsync($"/api/users/{_adminId}/password", new { password = "another-1" }),
            UserAdministration.DenyManageAdmin);
        await AssertBadRequest(
            await manager.DeleteAsync($"/api/users/{_adminId}"),
            UserAdministration.DenyManageAdmin);
    }

    [Fact]
    public async Task Disabled_Admin_Can_Be_Deleted_When_An_Enabled_Admin_Remains()
    {
        await EnsureSeedAsync();
        using var admin = await ClientAsync("admin", "admin123");

        var disabledId = await IdAsync(await admin.PostAsJsonAsync("/api/users", new { userName = "spare-admin", password = "secret123", displayName = "备用管理员", role = "Admin", enabled = true }));
        var disabled = await admin.PutAsJsonAsync($"/api/users/{disabledId}", new { displayName = "备用管理员", role = "Admin", enabled = false });
        Assert.True(disabled.IsSuccessStatusCode, await disabled.Content.ReadAsStringAsync());

        // 删除只统计启用中的管理员：已停用的管理员不挡住删除，只要现场还有启用中的管理员。
        using var deletedDisabled = await admin.DeleteAsync($"/api/users/{disabledId}");
        Assert.Equal(HttpStatusCode.NoContent, deletedDisabled.StatusCode);

        var extraId = await IdAsync(await admin.PostAsJsonAsync("/api/users", new { userName = "extra-admin", password = "secret123", displayName = "额外管理员", role = "Admin", enabled = true }));

        using var manager = await ClientAsync("usermgr", "secret123");
        await AssertBadRequest(await manager.DeleteAsync($"/api/users/{extraId}"), UserAdministration.DenyManageAdmin);

        using var deletedExtra = await admin.DeleteAsync($"/api/users/{extraId}");
        Assert.Equal(HttpStatusCode.NoContent, deletedExtra.StatusCode);
    }
}
