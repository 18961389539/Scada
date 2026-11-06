using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Scada.Server.Tests;

// 区域授权审计：不读端点实现，也不依赖具体业务断言，而是直接遍历路由表。
// 做法是在 B 区造一套带唯一哨兵串的数据，然后用"只有 A 区权限"的账号请求端点：
// 任何没做区域过滤的端点，都会把哨兵（或 B 区对象）原样带回来。
// 这比逐个翻端点的 if 更可靠——漏掉的那个端点不需要任何人记得它存在。
public sealed class AreaScopeAuditTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    private readonly ITestOutputHelper _output;
    public AreaScopeAuditTests(AuthorizationTests.Site site, ITestOutputHelper output) { _site = site; _output = output; }

    [Fact]
    public async Task ReadEndpointsDoNotReturnOtherAreasWithoutExplicitScope()
    {
        var marker = "SENTINEL" + Guid.NewGuid().ToString("N")[..8];
        var admin = await _site.ClientAsync("admin");

        var deviceResponse = await admin.PostAsJsonAsync("/api/devices", AuthorizationTests.Site.DeviceRequest(marker + "设备", _site.AreaB));
        Assert.True(deviceResponse.IsSuccessStatusCode, await deviceResponse.Content.ReadAsStringAsync());
        var deviceId = (await deviceResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var tagResponse = await admin.PostAsJsonAsync("/api/tags", _site.TagBody(deviceId, marker + "测点"));
        Assert.True(tagResponse.IsSuccessStatusCode, await tagResponse.Content.ReadAsStringAsync());

        var op = await _site.ClientAsync("opA");
        var (leaks, scanned, skipped) = await AuditListAsync(op, marker);

        // 先确认真的扫到了端点：拿不到路由表时这个测试会"静默通过"，比失败更危险。
        _output.WriteLine($"扫描 {scanned.Count} 个无参 GET 端点");
        _output.WriteLine($"跳过 {skipped.Count} 个带参端点（由详情端点用例覆盖）");
        Assert.True(scanned.Count >= 15, $"只扫描到 {scanned.Count} 个无参只读端点，端点发现可能失效：{string.Join(", ", scanned)}");
        Assert.True(leaks.Count == 0,
            "以下只读端点在没有区域参数时，把其它区域的数据返回给了只有 A 区权限的用户：\n" + string.Join("\n", leaks));
    }

    // 详情端点：用 B 区真实对象的 ID 探测。返回 200 就意味着"知道 ID 就能读"，这是越权读取最常见的入口。
    // 只对有区域语义的对象造数据；全局资源（画面、布局、用户、备份、项目版本、OPC 证书）本来就不按区域隔离，
    // 用 B 区 ID 去试只会得到 404，测不出任何东西——这些端点登记在 uncovered 清单里，不静默跳过。
    // /api/commands/{id} 需要"网关托管设备"才会产生 GatewayCommand，本地采集设备的命令不进这张表，
    // 所以也归入待补：要测它得先配一个网关并让设备挂到网关下。
    [Fact]
    public async Task DetailEndpointsRejectOtherAreasObjects()
    {
        var admin = await _site.ClientAsync("admin");
        var op = await _site.ClientAsync("opA");
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // B 区一套最小数据：设备 → Bool 测点（信号盘输出）→ 信号盘 / 配方模板。
        var deviceId = await CreateAsync(admin, "/api/devices", new
        {
            name = "B-审计设备" + suffix, protocol = "ModbusTcp", host = "127.0.0.1", port = 1502, stationNumber = 1,
            byteOrder = "ABCD", pollIntervalMs = 1000, timeoutMs = 500, enabled = true, areaId = _site.AreaB
        }, "id");
        var hornId = deviceId is null ? null : await CreateAsync(admin, "/api/tags", TagBody(deviceId.Value, "B-喇叭" + suffix), "id");
        var panelId = hornId is null ? null : await CreateAsync(admin, "/api/annunciators", new
        {
            name = "B-信号盘" + suffix, areaId = _site.AreaB, enabled = true, latchMode = "Follow", silenceMinutes = 5,
            flashOnMs = 400, flashOffMs = 400, criticalFlashOnMs = 200, criticalFlashOffMs = 200, browserSound = false,
            outputs = new object[] { new { kind = "Horn", tagId = hornId!.Value, activeValue = 1.0, inactiveValue = 0.0, enabled = true } },
            levels = new object[]
            {
                new { level = "Critical", horn = true, beacon = false, towerRed = false, towerYellow = false, towerBlue = false, towerGreen = false, flash = true, soundKey = "critical" }
            }
        }, "id");
        var jobId = await CreateAsync(admin, "/api/calc-jobs", new
        {
            name = "B-计算" + suffix, kind = "Accumulator", areaId = _site.AreaB,
            inputTagId = _site.TagB, intervalMs = 1000
        }, "id");
        var templateId = await CreateAsync(admin, "/api/report-templates", new
        {
            name = "B-报表" + suffix, period = "Day", areaId = _site.AreaB,
            columns = new object[] { new { key = "avg", title = "平均", kind = "TagAvg", tagId = _site.TagB, decimals = 2 } }
        }, "id");
        var reportRunId = templateId is null ? null : await CreateAsync(admin, $"/api/report-templates/{templateId}/generate", new { }, "id");
        var sequenceId = hornId is null ? null : await CreateAsync(admin, "/api/control/sequences", new
        {
            name = "B-顺控" + suffix, areaId = _site.AreaB, dualApproval = false,
            steps = new[] { new { name = "开阀", tagId = hornId!.Value, value = 1.0, confirmExpression = $"#{hornId} == 1", timeoutMs = 60000, delayMs = 0 } }
        }, "id");
        var controlRunId = sequenceId is null ? null : await CreateAsync(admin, $"/api/control/sequences/{sequenceId}/start", new { confirmed = true }, "runId");
        var recipeId = await CreateAsync(admin, "/api/recipes/templates", new
        {
            name = "B-配方" + suffix, areaId = _site.AreaB, description = "", dualApproval = false,
            parameters = new object[] { new { key = "flow", name = "流量", tagId = _site.TagB, unit = "m3/h", min = 0.0, max = 100.0 } }
        }, "id");

        // 长前缀在前：/api/recipes/downloads/{id} 不能被 /api/recipes/ 的探针吃掉。
        // Id 为 0 表示"该前缀不按区域隔离"，只登记不探测。
        var probes = new (string Prefix, string? Value)[]
        {
            ("/api/recipes/downloads/", null),
            ("/api/recipes/templates", null),
            ("/api/recipes/", recipeId?.ToString()),
            ("/api/annunciators/", panelId?.ToString()),
            ("/api/devices/", (deviceId ?? _site.DeviceB).ToString()),
            ("/api/tags/", _site.TagB.ToString()),
            ("/api/alarms/", _site.AlarmB.ToString()),
            ("/api/gateways/", null),
            ("/api/calc-jobs/libraries/", null),
            ("/api/calc-jobs/", jobId?.ToString()),
            ("/api/control/", controlRunId?.ToString()),
            ("/api/screens/", null),
            ("/api/hmi/", null),
            ("/api/users/", null),
            ("/api/project/", null),
            ("/api/report-runs/", reportRunId?.ToString()),
            ("/api/object-", null),
            ("/api/opc/", null),
            ("/api/maintenance/", null),
            ("/api/commands/", null),
            ("/api/v1/tags/", _site.TagB.ToString()),
        };

        var leaks = new List<string>();
        var checkedPatterns = new List<string>();
        var uncovered = new List<string>();
        foreach (var endpoint in DetailEndpoints())
        {
            var pattern = endpoint.RoutePattern.RawText!;
            var probe = probes.FirstOrDefault(x => pattern.StartsWith(x.Prefix, StringComparison.Ordinal));
            if (probe.Prefix is null) { uncovered.Add($"{pattern}（没有匹配的探针前缀）"); continue; }
            if (probe.Value is null) { uncovered.Add($"{pattern}（全局资源或需要额外前置数据）"); continue; }
            checkedPatterns.Add(pattern);

            // 多占位符端点（versions/{id}/{revision}）统一用探测 ID 填充：只要第一层放行，就已经是越权。
            var url = Regex.Replace(pattern, @"\{[^}]+\}", probe.Value);
            using var response = await op.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.OK)
                leaks.Add($"{pattern} => 200，能读到 B 区对象 #{probe.Value}");
        }

        _output.WriteLine($"探测 {checkedPatterns.Count} 个详情端点：{string.Join(", ", checkedPatterns)}");
        _output.WriteLine($"未纳入探测的 {uncovered.Count} 个带参端点：{string.Join(", ", uncovered)}");
        Assert.True(checkedPatterns.Count >= 6, $"只探测到 {checkedPatterns.Count} 个详情端点，探测逻辑可能失效");
        Assert.True(leaks.Count == 0, "以下详情端点允许只有 A 区权限的用户读取其它区域的对象：\n" + string.Join("\n", leaks));
    }

    private IEnumerable<RouteEndpoint> DetailEndpoints() =>
        _site.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint =>
            {
                var pattern = endpoint.RoutePattern.RawText;
                if (pattern is null || !pattern.StartsWith("/api/", StringComparison.Ordinal) || !pattern.Contains('{')) return false;
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                return methods is not null && methods.Contains("GET", StringComparer.OrdinalIgnoreCase);
            });

    private async Task<(List<string> Leaks, List<string> Scanned, List<string> Skipped)> AuditListAsync(HttpClient client, string marker)
    {
        var leaks = new List<string>();
        var scanned = new List<string>();
        var skipped = new List<string>();
        var endpoints = _site.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>();

        foreach (var endpoint in endpoints)
        {
            var pattern = endpoint.RoutePattern.RawText;
            if (pattern is null || !pattern.StartsWith("/api/", StringComparison.Ordinal)) continue;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || !methods.Contains("GET", StringComparer.OrdinalIgnoreCase)) continue;
            if (pattern.Contains('{')) { skipped.Add(pattern); continue; }
            scanned.Add(pattern);

            using var response = await client.GetAsync(pattern);
            if (!response.IsSuccessStatusCode) continue;
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true) continue;
            var body = await response.Content.ReadAsStringAsync();
            if (body.Contains(marker, StringComparison.Ordinal)) leaks.Add($"{pattern}");
        }

        return (leaks, scanned, skipped);
    }

    // 创建对象并取回指定字段；失败只记录不中断——审计的价值在于"能测的都测到"，
    // 某类对象因请求体变化建不出来时，应该出现在输出里，而不是让整个用例红掉。
    private async Task<int?> CreateAsync(HttpClient admin, string path, object body, string field)
    {
        using var response = await admin.PostAsJsonAsync(path, body);
        if (!response.IsSuccessStatusCode)
        {
            _output.WriteLine($"创建 {path} 失败（{(int)response.StatusCode}）：{await response.Content.ReadAsStringAsync()}");
            return null;
        }
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.TryGetProperty(field, out var value) && value.TryGetInt32(out var id) ? id : null;
    }

    private static object TagBody(int deviceId, string name) => new
    {
        deviceId, name, area = "HoldingRegister", address = 0, dataType = "Bool", scale = 1.0, offset = 0.0,
        writable = true, archiveMode = "None", deadband = 0.0, archivePeriodSec = 600
    };
}
