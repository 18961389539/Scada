using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Data;

namespace Scada.Server.Tests;

// 主数据维护工具（区域/设备/测点/报警规则）的端到端覆盖：与 REST 端点同一套校验、热加载与审计。
public sealed class McpMasterDataTests : IClassFixture<McpApiTests.Site>
{
    private readonly McpApiTests.Site _site;
    public McpMasterDataTests(McpApiTests.Site site) => _site = site;

    [Fact]
    public async Task Master_Data_Roundtrip_Creates_Updates_And_Deletes()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var area = Ok(await McpApiTests.CallAsync(client, "create_area",
                new { name = "MCP-A-" + suffix, description = "created by mcp test" }));
            var areaId = area.GetProperty("areaId").GetInt32();
            Assert.True(areaId > 0);

            var renamed = Ok(await McpApiTests.CallAsync(client, "update_area",
                new { areaId, name = "MCP-A2-" + suffix, description = "" }));
            Assert.Equal("MCP-A2-" + suffix, renamed.GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Null, renamed.GetProperty("description").ValueKind);

            var device = Ok(await McpApiTests.CallAsync(client, "create_device", new
            {
                name = "MCP-D-" + suffix, protocol = "ModbusTcp", host = "127.0.0.1", port = 1502,
                stationNumber = 1, areaId
            }));
            var deviceId = device.GetProperty("deviceId").GetInt32();
            Assert.True(deviceId > 0);
            Assert.Equal(areaId, device.GetProperty("areaId").GetInt32());

            var tag = Ok(await McpApiTests.CallAsync(client, "create_tag", new
            {
                deviceId, name = "MCP-T-" + suffix, area = "HoldingRegister", dataType = "UInt16",
                address = 10, scale = 1, offset = 0, unit = "s", writable = true, archiveMode = "OnChange"
            }));
            var tagId = tag.GetProperty("tagId").GetInt32();
            Assert.True(tagId > 0);

            var moved = Ok(await McpApiTests.CallAsync(client, "update_tag",
                new { tagId, address = 20, description = "moved register" }));
            Assert.Equal("MCP-T-" + suffix, moved.GetProperty("name").GetString());

            var rule = Ok(await McpApiTests.CallAsync(client, "create_alarm_rule", new
            {
                tagId, type = "High", level = "Warning", threshold = 12.5, delaySec = 3, enabled = true
            }));
            var ruleId = rule.GetProperty("ruleId").GetInt32();
            Assert.True(ruleId > 0);

            var tuned = Ok(await McpApiTests.CallAsync(client, "update_alarm_rule",
                new { ruleId, threshold = 15.0, enabled = false }));
            Assert.Equal(tagId, tuned.GetProperty("tagId").GetInt32());

            Ok(await McpApiTests.CallAsync(client, "delete_alarm_rule", new { ruleId }));
            Ok(await McpApiTests.CallAsync(client, "delete_tag", new { tagId }));
            Ok(await McpApiTests.CallAsync(client, "delete_device", new { deviceId }));
            Ok(await McpApiTests.CallAsync(client, "delete_area", new { areaId }));

            await using var db = await _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>()
                .CreateDbContextAsync();
            Assert.False(await db.Areas.AnyAsync(x => x.Id == areaId));
            Assert.False(await db.Devices.AnyAsync(x => x.Id == deviceId));
            Assert.False(await db.Tags.AnyAsync(x => x.Id == tagId));
            Assert.False(await db.AlarmRules.AnyAsync(x => x.Id == ruleId));
            // 每次写入都留下 mcp:lan 的领域审计，且带字段差分。
            var audit = await db.OperationLogs.AsNoTracking()
                .Where(x => x.UserName == "mcp:lan" && x.TargetType == "Tag")
                .OrderByDescending(x => x.Id).FirstAsync();
            Assert.StartsWith("删除测点", audit.Action);
            Assert.False(string.IsNullOrEmpty(audit.Details));
        }
    }

    [Fact]
    public async Task Master_Data_Conflicts_Return_Structured_Failures()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var name = "MCP-DUP-" + Guid.NewGuid().ToString("N")[..8];
            var area = Ok(await McpApiTests.CallAsync(client, "create_area", new { name }));
            var areaId = area.GetProperty("areaId").GetInt32();

            var duplicate = await McpApiTests.CallAsync(client, "create_area", new { name });
            Assert.False(duplicate.GetProperty("structuredContent").GetProperty("success").GetBoolean());
            Assert.Contains("已存在", duplicate.GetProperty("structuredContent").GetProperty("message").GetString());

            var device = Ok(await McpApiTests.CallAsync(client, "create_device", new
            {
                name = name + "-D", protocol = "ModbusTcp", host = "127.0.0.1", port = 1502, areaId
            }));
            var deviceId = device.GetProperty("deviceId").GetInt32();
            Ok(await McpApiTests.CallAsync(client, "create_tag",
                new { deviceId, name = name + "-T", area = "HoldingRegister", dataType = "UInt16", address = 1 }));

            var duplicateTag = await McpApiTests.CallAsync(client, "create_tag",
                new { deviceId, name = name + "-T", area = "HoldingRegister", dataType = "UInt16", address = 2 });
            Assert.False(duplicateTag.GetProperty("structuredContent").GetProperty("success").GetBoolean());

            // 区域内还有设备时不能删除。
            var blocked = await McpApiTests.CallAsync(client, "delete_area", new { areaId });
            Assert.False(blocked.GetProperty("structuredContent").GetProperty("success").GetBoolean());
            Assert.Contains("设备", blocked.GetProperty("structuredContent").GetProperty("message").GetString());

            // 测点写错地址：倍率为 0 的可写测点被配置校验拒绝，而不是静默写入。
            var invalid = await McpApiTests.CallAsync(client, "create_tag", new
            {
                deviceId, name = name + "-BAD", area = "HoldingRegister", dataType = "UInt16", address = 3,
                writable = true, scale = 0.0
            });
            Assert.False(invalid.GetProperty("structuredContent").GetProperty("success").GetBoolean());

            var missing = await McpApiTests.CallAsync(client, "delete_device", new { deviceId = 999999 });
            Assert.True(missing.GetProperty("isError").GetBoolean());
            Assert.Equal("not_found",
                missing.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Master_Data_Rejects_Invalid_Device_Input()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var badPort = await McpApiTests.CallAsync(client, "create_device", new
            {
                name = "MCP-BAD-" + Guid.NewGuid().ToString("N")[..8], protocol = "ModbusTcp",
                host = "127.0.0.1", port = 70000
            });
            Assert.False(badPort.GetProperty("structuredContent").GetProperty("success").GetBoolean());

            var noStation = await McpApiTests.CallAsync(client, "create_device", new
            {
                name = "MCP-BAD2-" + Guid.NewGuid().ToString("N")[..8], protocol = "ModbusRtu",
                serialPort = "COMX", baudRate = 9600, stationNumber = 300
            });
            Assert.True(noStation.GetProperty("isError").GetBoolean());
            Assert.Equal("invalid_argument",
                noStation.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Write_Retry_With_Same_RequestId_Returns_The_First_Result_Only()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var requestId = Guid.NewGuid();
            var threshold = 40 + Random.Shared.Next(1, 50);
            var arguments = new { tagId = _site.TagA, type = "High", level = "Warning", threshold, requestId };
            var first = Ok(await McpApiTests.CallAsync(client, "create_alarm_rule", arguments));
            var second = Ok(await McpApiTests.CallAsync(client, "create_alarm_rule", arguments));
            var ruleId = first.GetProperty("ruleId").GetInt32();
            Assert.Equal(ruleId, second.GetProperty("ruleId").GetInt32());

            var factory = _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            // 重放不产生第二条规则；两条调用各自仍留一条 MCP 审计。
            Assert.Equal(1, await db.AlarmRules.CountAsync(x => x.Id == ruleId));
            Assert.Equal(1, await db.AlarmRules.CountAsync(x => x.TagId == _site.TagA && x.Threshold == threshold));
        }
    }

    [Fact]
    public async Task Empty_RequestId_Is_Rejected_Before_Any_Write()
    {
        var client = _site.Server.CreateClient();
        using (client)
        {
            var result = await McpApiTests.CallAsync(client, "create_alarm_rule", new
            {
                tagId = _site.TagA, type = "High", level = "Warning", threshold = 11.0, requestId = Guid.Empty
            });
            Assert.True(result.GetProperty("isError").GetBoolean(), result.ToString());
            Assert.Equal("invalid_argument",
                result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString());
        }
    }

    private static JsonElement Ok(JsonElement result)
    {
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), result.ToString());
        var payload = result.GetProperty("structuredContent");
        Assert.True(payload.GetProperty("success").GetBoolean(), result.ToString());
        return payload;
    }
}
