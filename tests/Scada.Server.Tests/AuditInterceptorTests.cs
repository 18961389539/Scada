using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Data;

namespace Scada.Server.Tests;

// 配置实体的增删改现在由 AuditInterceptor 统一落审计：REST 与 MCP 各写一次，
// 断言「一个写入口一条审计、操作者正确、差分落在 Details」，以及批量路径只留一条汇总。
public sealed class AuditInterceptorTests : IClassFixture<AuditInterceptorTests.Site>
{
    // 默认姿态不注册写工具且只允许回环；审计用例要显式打开写能力。
    public sealed class Site : AuthorizationTests.Site
    {
        protected override void ConfigureSettings(IWebHostBuilder builder)
        {
            builder.UseSetting("Mcp:RequestsPerMinute", "1000");
            builder.UseSetting("Mcp:AllowedCidrs:0", "*");
            builder.UseSetting("Mcp:AllowConfigurationWrites", "true");
        }
    }

    private readonly Site _site;
    public AuditInterceptorTests(Site site) => _site = site;

    [Fact]
    public async Task DeviceWritesLeaveOneAuditRowWithTheRequestUser()
    {
        var admin = await _site.ClientAsync("admin");
        var name = "审计设备" + Guid.NewGuid().ToString("N")[..6];

        var created = await admin.PostAsJsonAsync("/api/devices", AuthorizationTests.Site.DeviceRequest(name, _site.AreaA));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var createRow = await SingleAsync("创建设备：" + name);
        Assert.Equal("admin", createRow.UserName);
        Assert.Equal("Device", createRow.TargetType);
        Assert.Equal(id.ToString(), createRow.TargetId);
        Assert.Contains("\"op\":\"create\"", createRow.Details);

        // 只有真正改了字段才会产生「修改」审计：拦截器跳过没有属性变更的 Modified 条目。
        var updated = await admin.PutAsJsonAsync($"/api/devices/{id}", new
        {
            name, protocol = "ModbusTcp", host = "127.0.0.1", port = 1502, stationNumber = 1,
            byteOrder = "ABCD", pollIntervalMs = 1000, timeoutMs = 500, enabled = true, areaId = _site.AreaA
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updateRow = await SingleAsync("修改设备：" + name);
        Assert.Contains("\"enabled\"", updateRow.Details);
        Assert.Contains("\"op\":\"update\"", updateRow.Details);

        var deleted = await admin.DeleteAsync($"/api/devices/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Contains("\"op\":\"delete\"", (await SingleAsync("删除设备：" + name)).Details);
    }

    [Fact]
    public async Task McpDeviceWritesAreAuditedAsMcpActor()
    {
        using var mcp = _site.Server.CreateClient();
        var name = "MCP审计设备" + Guid.NewGuid().ToString("N")[..6];
        var created = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "create_device", new
        {
            name, protocol = "ModbusTcp", host = "127.0.0.1", port = 1502, stationNumber = 1, areaId = _site.AreaA
        }));
        Assert.True(created.GetProperty("success").GetBoolean(), created.ToString());
        var id = created.GetProperty("deviceId").GetInt32();

        var row = await SingleAsync("创建设备：" + name);
        Assert.Equal("mcp:lan", row.UserName);
        Assert.Null(row.UserId);
        Assert.Equal(id.ToString(), row.TargetId);

        var deleted = McpApiTests.Data(await McpApiTests.CallAsync(mcp, "delete_device", new { deviceId = id }));
        Assert.True(deleted.GetProperty("success").GetBoolean(), deleted.ToString());
        Assert.Equal("mcp:lan", (await SingleAsync("删除设备：" + name)).UserName);
    }

    [Fact]
    public async Task TagWritesAreAuditedButBatchCreateStaysOneSummaryRow()
    {
        var admin = await _site.ClientAsync("admin");
        var name = "审计测点" + Guid.NewGuid().ToString("N")[..6];

        var created = await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, name));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var createRow = await SingleAsync("创建测点：" + name);
        Assert.Equal("Tag", createRow.TargetType);
        Assert.Equal(id.ToString(), createRow.TargetId);

        var updated = await admin.PutAsJsonAsync($"/api/tags/{id}", new
        {
            deviceId = _site.DeviceA, name, description = "改过描述", area = "HoldingRegister", address = 0,
            dataType = "Float", scale = 1.0, offset = 0.0, writable = true, archiveMode = "None",
            deadband = 0.0, archivePeriodSec = 600
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Contains("\"description\"", (await SingleAsync("修改测点：" + name)).Details);

        // 批量建点最多 500 个：逐条审计会淹没记录，只保留一条汇总。
        var batch = await admin.PostAsJsonAsync("/api/tags/batch", new
        {
            deviceId = _site.DeviceA,
            tags = new object[]
            {
                new { name = name + "-1", area = "HoldingRegister", address = 10, dataType = "Float" },
                new { name = name + "-2", area = "HoldingRegister", address = 12, dataType = "Float" }
            }
        });
        Assert.Equal(HttpStatusCode.OK, batch.StatusCode);
        var factory = _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.False(await db.OperationLogs.AnyAsync(x => x.Action == "创建测点：" + name + "-1"));
            Assert.True(await db.OperationLogs.AnyAsync(x => x.Action.StartsWith("批量创建测点：")));
        }

        var deleted = await admin.DeleteAsync($"/api/tags/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Contains("\"op\":\"delete\"", (await SingleAsync("删除测点：" + name)).Details);
    }

    [Fact]
    public async Task AlarmRuleWritesAreAuditedAndOutOfServiceKeepsItsBusinessText()
    {
        var admin = await _site.ClientAsync("admin");
        var created = await admin.PostAsJsonAsync("/api/alarm-rules", new
        {
            tagId = _site.TagA, type = "High", level = "Warning", threshold = 10.0,
            delaySec = 0, recoverDeadband = 0.0, enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var createRow = await SingleAsync($"创建报警规则：测点 #{_site.TagA}");
        Assert.Equal("AlarmRule", createRow.TargetType);
        Assert.Equal(id.ToString(), createRow.TargetId);

        // 停用是业务动作：保留「停用报警规则 #id」这条手工审计，同时拦截器被抑制，
        // 不会再多出一条通用的「修改报警规则」。
        var oos = await admin.PostAsJsonAsync($"/api/alarm-rules/{id}/out-of-service", new { outOfService = true, reason = "仪表检修" });
        Assert.Equal(HttpStatusCode.OK, oos.StatusCode);
        var logs = await AllAsync();
        Assert.Contains(logs, x => x.Action == $"停用报警规则 #{id}");
        Assert.DoesNotContain(logs, x => x.Action == $"修改报警规则：测点 #{_site.TagA}");

        var deleted = await admin.DeleteAsync($"/api/alarm-rules/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(id.ToString(), (await SingleAsync($"删除报警规则：测点 #{_site.TagA}")).TargetId);
    }

    [Fact]
    public async Task AreaAndRoleWritesUseEntityTargetTypes()
    {
        var admin = await _site.ClientAsync("admin");
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var areaName = "审计区域" + suffix;
        var areaResponse = await admin.PostAsJsonAsync("/api/areas", new { name = areaName });
        Assert.Equal(HttpStatusCode.Created, areaResponse.StatusCode);
        var areaId = (await areaResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var areaRow = await SingleAsync("创建区域：" + areaName);
        Assert.Equal("Area", areaRow.TargetType);
        Assert.Equal(areaId.ToString(), areaRow.TargetId);

        var roleName = "审计角色" + suffix;
        var roleResponse = await admin.PostAsJsonAsync("/api/roles", new { name = roleName, permissions = new[] { "View" } });
        Assert.Equal(HttpStatusCode.Created, roleResponse.StatusCode);
        var roleId = (await roleResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal("Role", (await SingleAsync("创建角色：" + roleName)).TargetType);

        // 权限是 flags 枚举，差分里应出现新增的 Control。
        var put = await admin.PutAsJsonAsync($"/api/roles/{roleId}", new { name = roleName, permissions = new[] { "View", "Control" } });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Contains("Control", (await SingleAsync("修改角色：" + roleName)).Details);

        var deleted = await admin.DeleteAsync($"/api/areas/{areaId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal("Area", (await SingleAsync("删除区域：" + areaName)).TargetType);
    }

    [Fact]
    public async Task SparePartWritesAreAuditedAndStockMovementKeepsItsBusinessText()
    {
        var admin = await _site.ClientAsync("admin");
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var code = "SP-" + suffix;
        var name = "审计备件" + suffix;
        var created = await admin.PostAsJsonAsync("/api/spare-parts", new
        {
            code, name, specification = "", unit = "个", stock = 1.0, minStock = 0.0,
            location = "", supplier = "", description = "", enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var createRow = await SingleAsync($"创建备件：{name}");
        Assert.Equal("SparePart", createRow.TargetType);
        Assert.Equal(id.ToString(), createRow.TargetId);

        // 出入库是业务动作：保留「备件出入库」这条手工审计，同时拦截器被抑制，不会再多一条「修改备件」。
        var movement = await admin.PostAsJsonAsync($"/api/spare-parts/{id}/movements", new { type = "In", quantity = 2.0, note = "盘点补录" });
        Assert.Equal(HttpStatusCode.OK, movement.StatusCode);
        var logs = await AllAsync();
        Assert.Contains(logs, x => x.Action.StartsWith($"备件出入库：{name}"));
        Assert.DoesNotContain(logs, x => x.Action == $"修改备件：{name}");

        // 有出入库流水的备件不允许删除；删除审计用另一个没有流水的备件验证。
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/spare-parts/{id}")).StatusCode);
        var deleteName = "待删备件" + suffix;
        var second = await admin.PostAsJsonAsync("/api/spare-parts", new
        {
            code = "SP2-" + suffix, name = deleteName, specification = "", unit = "个", stock = 0.0, minStock = 0.0,
            location = "", supplier = "", description = "", enabled = true
        });
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var deleted = await admin.DeleteAsync($"/api/spare-parts/{secondId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(secondId.ToString(), (await SingleAsync($"删除备件：{deleteName}")).TargetId);
    }

    private async Task<OperationLog> SingleAsync(string action)
    {
        var factory = _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.OperationLogs.AsNoTracking().SingleAsync(x => x.Action == action);
    }

    private async Task<List<OperationLog>> AllAsync()
    {
        var factory = _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.OperationLogs.AsNoTracking().ToListAsync();
    }
}
