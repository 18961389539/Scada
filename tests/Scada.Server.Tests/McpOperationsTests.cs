using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Mcp;
using static Scada.Server.Tests.McpApiTests;

namespace Scada.Server.Tests;

public sealed class McpOperationsTests : IClassFixture<McpOperationsTests.Site>
{
    private readonly Site _site;
    public McpOperationsTests(Site site) => _site = site;

    public sealed class Site : McpApiTests.Site
    {
        public int RuleId, TagC, GatewayId;

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            var factory = Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            var rule = new AlarmRule
            {
                TagId = TagA, Type = AlarmRuleType.High, Threshold = 50, Level = AlarmLevel.Warning,
                Enabled = true, Message = "MCP high alarm"
            };
            var tag = new Tag
            {
                Name = "MCP filter tag", DeviceId = DeviceB, DataType = TagDataType.Int32, Unit = "kPa",
                Writable = false, Address = 10, ArchiveMode = ArchiveMode.None
            };
            var gateway = new AcquisitionGateway { Name = "MCP gateway", TokenHash = "secret-token-hash" };
            db.AlarmRules.Add(rule);
            db.Tags.Add(tag);
            db.Gateways.Add(gateway);
            db.AlarmActivities.Add(new AlarmActivity
            {
                AlarmEventId = AlarmA, Ts = ScadaTime.NowMs(), User = "opA", Action = "comment", Note = "MCP note"
            });
            db.AlarmNotificationLogs.Add(new AlarmNotificationLog
            {
                AlarmEventId = AlarmA, RouteId = 7, ChannelId = 3, Action = "raise", Ts = ScadaTime.NowMs(),
                Success = false, Error = "simulated"
            });
            await db.SaveChangesAsync();
            RuleId = rule.Id; TagC = tag.Id; GatewayId = gateway.Id;
        }
    }

    [Fact]
    public async Task Health_Snapshot_Is_Read_Only_And_Omits_Secrets()
    {
        using var client = _site.Server.CreateClient();
        var data = McpApiTests.Data(await CallAsync(client, "query_health"));
        Assert.Contains(data.GetProperty("status").GetString(), new[] { "ok", "degraded" });
        Assert.True(data.GetProperty("configuredTags").GetInt32() >= 2);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("warnings").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("disk").GetProperty("root").GetString()));
        Assert.Equal(JsonValueKind.Array, data.GetProperty("offlineDevices").ValueKind);
        var json = data.ToString();
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Alarm_Summary_Reports_Live_Counts_And_Flood_Guard()
    {
        using var client = _site.Server.CreateClient();
        var data = McpApiTests.Data(await CallAsync(client, "get_alarm_summary"));
        Assert.True(data.GetProperty("unack").GetInt32() >= 2);
        Assert.True(data.GetProperty("ackableUnack").GetInt32() >= 2);
        Assert.Equal(0, data.GetProperty("shelvedRules").GetInt32());
        Assert.Equal(0, data.GetProperty("outOfServiceRules").GetInt32());
        var flood = data.GetProperty("flood");
        Assert.True(flood.GetProperty("count").GetInt32() >= 2);
        Assert.False(flood.GetProperty("inFlood").GetBoolean());
        var guard = data.GetProperty("floodGuard");
        Assert.True(guard.GetProperty("autoSuppress").GetBoolean());
        Assert.True(guard.GetProperty("firstOut").GetBoolean());
        Assert.Equal("Warning", guard.GetProperty("suppressMaxLevel").GetString());
        Assert.False(guard.GetProperty("anyInFlood").GetBoolean());
        Assert.Equal(JsonValueKind.Array, guard.GetProperty("areas").ValueKind);
    }

    [Fact]
    public async Task Alarm_Timeline_Includes_Activities_And_Notifications()
    {
        using var client = _site.Server.CreateClient();
        var timeline = McpApiTests.Data(await CallAsync(client, "get_alarm_timeline", new { alarmId = _site.AlarmA }));
        Assert.Equal(_site.AlarmA, timeline.GetProperty("alarm").GetProperty("id").GetInt32());
        Assert.Equal("Unack", timeline.GetProperty("alarm").GetProperty("state").GetString());
        var activity = Assert.Single(timeline.GetProperty("activities").EnumerateArray());
        Assert.Equal("MCP note", activity.GetProperty("note").GetString());
        var notification = Assert.Single(timeline.GetProperty("notifications").EnumerateArray());
        Assert.Equal("simulated", notification.GetProperty("error").GetString());
        Assert.False(notification.GetProperty("success").GetBoolean());
        Assert.False(timeline.GetProperty("activitiesTruncated").GetBoolean());
        Assert.False(timeline.GetProperty("truncated").GetBoolean());
        Assert.True(timeline.GetProperty("dataAsOf").GetInt64() > 0);
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "get_alarm_timeline", new { alarmId = int.MaxValue })));
    }

    [Fact]
    public async Task Alarm_Rules_Are_Paged_Filtered_And_Include_Out_Of_Service_State()
    {
        using var client = _site.Server.CreateClient();
        var rules = McpApiTests.Data(await CallAsync(client, "list_alarm_rules", new { tagId = _site.TagA }));
        var rule = Assert.Single(rules.GetProperty("items").EnumerateArray());
        Assert.Equal(_site.RuleId, rule.GetProperty("id").GetInt32());
        Assert.Equal("High", rule.GetProperty("type").GetString());
        Assert.Equal(50, rule.GetProperty("threshold").GetDouble());
        Assert.False(rule.GetProperty("outOfService").GetBoolean());
        Assert.Empty(McpApiTests.Data(await CallAsync(client, "list_alarm_rules", new { enabled = false })).GetProperty("items").EnumerateArray());
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "list_alarm_rules", new { tagId = int.MaxValue })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "list_alarm_rules", new { limit = 101 })));
    }

    [Fact]
    public async Task Tag_Search_Filters_By_Area_Type_Unit_And_Writability()
    {
        using var client = _site.Server.CreateClient();
        var area = McpApiTests.Data(await CallAsync(client, "search_tags", new { areaId = _site.AreaB }));
        var areaItems = area.GetProperty("items").EnumerateArray().ToArray();
        Assert.Contains(areaItems, x => x.GetProperty("id").GetInt32() == _site.TagB);
        Assert.DoesNotContain(areaItems, x => x.GetProperty("id").GetInt32() == _site.TagA);
        Assert.All(areaItems, x => Assert.Equal(_site.AreaB, x.GetProperty("areaId").GetInt32()));
        var filtered = McpApiTests.Data(await CallAsync(client, "search_tags", new { dataType = "Int32", unit = "kPa", writable = false }));
        var tag = Assert.Single(filtered.GetProperty("items").EnumerateArray());
        Assert.Equal(_site.TagC, tag.GetProperty("id").GetInt32());
        Assert.Equal(_site.AreaB, tag.GetProperty("areaId").GetInt32());
        Assert.False(tag.GetProperty("writable").GetBoolean());
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "search_tags", new { areaId = int.MaxValue })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "search_tags", new { unit = new string('x', 51) })));
    }

    [Fact]
    public async Task Areas_Are_Listed_And_Exposed_As_A_Resource()
    {
        using var client = _site.Server.CreateClient();
        var areas = McpApiTests.Data(await CallAsync(client, "list_areas"));
        var areaA = Assert.Single(areas.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetInt32() == _site.AreaA);
        Assert.Equal(1, areaA.GetProperty("deviceCount").GetInt32());
        Assert.True(areaA.GetProperty("tagCount").GetInt32() >= 1);
        Assert.False(areaA.GetProperty("isDefault").GetBoolean());
        Assert.False(areas.GetProperty("truncated").GetBoolean());
        Assert.True(areas.GetProperty("dataAsOf").GetInt64() > 0);
        var resource = await RpcAsync(client, "resources/read", new { uri = "scada://areas" });
        using var document = JsonDocument.Parse(resource.GetProperty("contents")[0].GetProperty("text").GetString()!);
        Assert.Contains(document.RootElement.GetProperty("items").EnumerateArray(),
            x => x.GetProperty("id").GetInt32() == _site.AreaB);
        Assert.True(document.RootElement.GetProperty("dataAsOf").GetInt64() > 0);
        Assert.NotNull(await _site.LastLogAsync(x => x.TargetType == "McpResource" && x.Action == "MCP scada://areas"));
    }

    [Fact]
    public async Task Gateway_Overview_Exposes_State_Without_Tokens()
    {
        using var client = _site.Server.CreateClient();
        var overview = McpApiTests.Data(await CallAsync(client, "get_gateway_overview"));
        var gateway = Assert.Single(overview.GetProperty("items").EnumerateArray());
        Assert.Equal(_site.GatewayId, gateway.GetProperty("id").GetInt32());
        Assert.Equal("MCP gateway", gateway.GetProperty("name").GetString());
        Assert.False(gateway.GetProperty("connected").GetBoolean());
        Assert.Equal(0, gateway.GetProperty("deviceCount").GetInt32());
        Assert.DoesNotContain("secret-token-hash", overview.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_Log_Is_Filtered_Paged_And_Omits_Change_Diffs()
    {
        var stamp = ScadaTime.NowMs();
        var user = "audit-" + Guid.NewGuid().ToString("N")[..8];
        var factory = _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.OperationLogs.AddRange(
                new OperationLog
                {
                    Ts = stamp - 3000, UserName = user, Action = "写入标签", TargetType = "Tag", TargetId = "1",
                    TagId = _site.TagA, OldValue = "0", NewValue = "9", Result = "成功",
                    Details = "{\"password\":\"secret\"}"
                },
                new OperationLog
                {
                    Ts = stamp - 2000, UserName = user, Action = "修改配置", TargetType = "ConfigVersion",
                    TargetId = "7", Result = "失败：拒绝"
                },
                new OperationLog
                {
                    Ts = stamp - 1000, UserName = "other-" + user, Action = "写入标签", TargetType = "Tag",
                    TargetId = "2", Result = "成功"
                });
            await db.SaveChangesAsync();
        }
        using var client = _site.Server.CreateClient();
        var page = McpApiTests.Data(await CallAsync(client, "query_audit_log",
            new { from = stamp - 10_000, to = stamp + 10_000, userName = user }));
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("失败：拒绝", items[0].GetProperty("result").GetString());
        Assert.False(items[0].GetProperty("hasDetails").GetBoolean());
        Assert.Equal("成功", items[1].GetProperty("result").GetString());
        Assert.True(items[1].GetProperty("hasDetails").GetBoolean());
        Assert.Equal("0", items[1].GetProperty("oldValue").GetString());
        Assert.Equal("9", items[1].GetProperty("newValue").GetString());
        Assert.DoesNotContain("password", page.ToString(), StringComparison.OrdinalIgnoreCase);
        var success = McpApiTests.Data(await CallAsync(client, "query_audit_log",
            new { from = stamp - 10_000, to = stamp + 10_000, userName = user, success = true }));
        Assert.Equal("成功", Assert.Single(success.GetProperty("items").EnumerateArray()).GetProperty("result").GetString());
        Assert.Single(McpApiTests.Data(await CallAsync(client, "query_audit_log",
            new { from = stamp - 10_000, to = stamp + 10_000, userName = user, keyword = "配置" })).GetProperty("items").EnumerateArray());
        var first = McpApiTests.Data(await CallAsync(client, "query_audit_log",
            new { from = stamp - 10_000, to = stamp + 10_000, userName = user, limit = 1 }));
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
        var second = McpApiTests.Data(await CallAsync(client, "query_audit_log",
            new { from = stamp - 10_000, to = stamp + 10_000, userName = user, offset = 1, limit = 1 }));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);
        Assert.True(page.GetProperty("dataAsOf").GetInt64() > 0);
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "query_audit_log",
            new { from = 0, to = 32L * 86_400_000 })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "query_audit_log",
            new { from = stamp - 10_000, to = stamp, offset = 10001 })));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "query_audit_log",
            new { from = stamp - 10_000, to = stamp, limit = 0 })));
    }

    [Fact]
    public async Task Operations_Context_Combines_Status_Problem_Tags_And_Active_Alarms()
    {
        var now = ScadaTime.NowMs();
        var cache = _site.Services.GetRequiredService<IRealtimeCache>();
        cache.Set(new TagValue(_site.TagA, 12.5, TagQuality.Bad, now - 10 * 60_000));
        cache.Set(new TagValue(_site.TagB, 7, TagQuality.Good, now - 60_000));
        using var client = _site.Server.CreateClient();
        var device = McpApiTests.Data(await CallAsync(client, "get_operations_context", new { deviceId = _site.DeviceA }));
        Assert.Equal("device", device.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(_site.DeviceA, device.GetProperty("scope").GetProperty("id").GetInt32());
        Assert.Equal(_site.AreaA, device.GetProperty("scope").GetProperty("areaId").GetInt32());
        Assert.Equal(_site.DeviceA, Assert.Single(device.GetProperty("devices").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.True(device.GetProperty("totalTags").GetInt32() >= 1);
        Assert.True(device.GetProperty("badQualityTags").GetInt32() >= 1);
        Assert.True(device.GetProperty("staleTags").GetInt32() >= 1);
        var problem = Assert.Single(device.GetProperty("problemTags").EnumerateArray(),
            x => x.GetProperty("tagId").GetInt32() == _site.TagA);
        Assert.Equal("Bad", problem.GetProperty("quality").GetString());
        Assert.InRange(problem.GetProperty("ageMs").GetInt64(), 10 * 60_000, 10 * 60_000 + 60_000);
        Assert.Equal(_site.AlarmA, Assert.Single(device.GetProperty("activeAlarms").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Equal(1, device.GetProperty("activeAlarmCount").GetInt32());
        Assert.Equal(1, device.GetProperty("unackAlarmCount").GetInt32());
        Assert.False(device.GetProperty("truncated").GetBoolean());
        Assert.True(device.GetProperty("dataAsOf").GetInt64() > 0);

        var area = McpApiTests.Data(await CallAsync(client, "get_operations_context", new { areaId = _site.AreaA }));
        Assert.Equal("area", area.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(_site.AreaA, area.GetProperty("scope").GetProperty("areaId").GetInt32());
        Assert.Contains(area.GetProperty("activeAlarms").EnumerateArray(), x => x.GetProperty("id").GetInt32() == _site.AlarmA);
        Assert.DoesNotContain(area.GetProperty("activeAlarms").EnumerateArray(), x => x.GetProperty("id").GetInt32() == _site.AlarmB);

        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "get_operations_context")));
        Assert.Equal("invalid_argument", ErrorCode(await CallAsync(client, "get_operations_context",
            new { deviceId = _site.DeviceA, areaId = _site.AreaA })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "get_operations_context", new { deviceId = int.MaxValue })));
        Assert.Equal("not_found", ErrorCode(await CallAsync(client, "get_operations_context", new { areaId = int.MaxValue })));
    }

    [Fact]
    public async Task Metrics_And_Contract_Resources_Are_Machine_Readable()
    {
        using var client = _site.Server.CreateClient();
        McpApiTests.Data(await CallAsync(client, "search_tags"));
        var metrics = await RpcAsync(client, "resources/read", new { uri = "scada://metrics" });
        using var metricsJson = JsonDocument.Parse(metrics.GetProperty("contents")[0].GetProperty("text").GetString()!);
        Assert.Equal(24, metricsJson.RootElement.GetProperty("windowHours").GetInt32());
        var tool = Assert.Single(metricsJson.RootElement.GetProperty("tools").EnumerateArray(),
            x => x.GetProperty("name").GetString() == "search_tags");
        Assert.True(tool.GetProperty("calls").GetInt32() >= 1);
        Assert.True(tool.GetProperty("success").GetInt32() >= 1);
        Assert.Equal(McpQueryBudget.Slots, metricsJson.RootElement.GetProperty("analysisBudget").GetProperty("maxConcurrent").GetInt32());
        Assert.True(metricsJson.RootElement.GetProperty("dataAsOf").GetInt64() > 0);

        var contract = await RpcAsync(client, "resources/read", new { uri = "scada://contract" });
        using var contractJson = JsonDocument.Parse(contract.GetProperty("contents")[0].GetProperty("text").GetString()!);
        Assert.Equal("1.2", contractJson.RootElement.GetProperty("contractVersion").GetString());
        Assert.False(contractJson.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.Equal("mcp:lan", contractJson.RootElement.GetProperty("writeActor").GetString());
        Assert.False(string.IsNullOrWhiteSpace(contractJson.RootElement.GetProperty("writeSemantics").GetProperty("pipeline").GetString()));
        var codes = contractJson.RootElement.GetProperty("errorCodes").EnumerateArray().ToArray();
        Assert.Equal(9, codes.Length);
        Assert.True(codes.Single(x => x.GetProperty("code").GetString() == McpErrorCodes.QueryBusy)
            .GetProperty("retryable").GetBoolean());
        Assert.False(codes.Single(x => x.GetProperty("code").GetString() == McpErrorCodes.InvalidArgument)
            .GetProperty("retryable").GetBoolean());
        Assert.Equal(65536, contractJson.RootElement.GetProperty("limits").GetProperty("maxRequestBytes").GetInt32());
    }

    private static string ErrorCode(JsonElement result)
    {
        Assert.True(result.GetProperty("isError").GetBoolean(), result.ToString());
        return result.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString()!;
    }
}
