using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data;
using Scada.Server.Services;

namespace Scada.Server.Tests;

// 测点反查、替换与画面发布引用校验（P0）。
public sealed class TagUsageTests : IClassFixture<ControlTests.ControlSite>
{
    private readonly ControlTests.ControlSite _site;
    public TagUsageTests(ControlTests.ControlSite site) => _site = site;
    private static string U() => Guid.NewGuid().ToString("N")[..5];
    private static async Task<JsonElement> Ok(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response.StatusCode is HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static object Node(string id, string type, int tagId) => new { id, type, x = 10, y = 10, width = 120, height = 80, tagId };
    private static object Document(params object[] nodes) => new { schemaVersion = 1, width = 1200, height = 720, background = "#101b2d", nodes };

    private async Task<int> CreateTagAsync(HttpClient admin, string prefix) =>
        (await Ok(await admin.PostAsJsonAsync("/api/tags", _site.TagBody(_site.DeviceA, prefix + U())))).GetProperty("id").GetInt32();

    private async Task<(int Id, int Revision)> CreateScreenAsync(HttpClient admin, object document, string name)
    {
        var created = await Ok(await admin.PostAsJsonAsync("/api/screens", new { name = name + U() }));
        var id = created.GetProperty("id").GetInt32();
        var saved = await Ok(await admin.PutAsJsonAsync($"/api/screens/{id}", new
        {
            name = name + U(), revision = created.GetProperty("revision").GetInt32(), document
        }));
        return (id, saved.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task TagDeleteIsBlockedUntilDraftReferencesAreRebound()
    {
        var admin = await _site.ClientAsync("admin");
        var tagA = await CreateTagAsync(admin, "反查");
        var tagB = await CreateTagAsync(admin, "目标");
        var (screenId, revision) = await CreateScreenAsync(admin, Document(Node("n1", "value", tagA)), "反查画面");

        var usage = await Ok(await admin.GetAsync($"/api/tags/{tagA}/usage"));
        Assert.Contains(usage.GetProperty("references").EnumerateArray(),
            item => item.GetProperty("category").GetString() == "screenDraft" && item.GetProperty("id").GetInt32() == screenId);

        var blocked = await admin.DeleteAsync($"/api/tags/{tagA}");
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("仍被", await blocked.Content.ReadAsStringAsync());

        var rebound = await Ok(await admin.PostAsJsonAsync($"/api/tags/{tagA}/rebind", new { targetTagId = tagB }));
        Assert.Equal(1, rebound.GetProperty("screens").GetInt32());
        Assert.Empty(rebound.GetProperty("publishedScreens").EnumerateArray());

        var after = await Ok(await admin.GetAsync($"/api/tags/{tagA}/usage"));
        Assert.Empty(after.GetProperty("references").EnumerateArray());
        await Ok(await admin.DeleteAsync($"/api/tags/{tagA}"));

        // 草稿已经指向目标测点，原 revision 仍在乐观锁范围内。
        var detail = await Ok(await admin.GetAsync($"/api/screens/{screenId}?draft=true"));
        Assert.True(detail.GetProperty("revision").GetInt32() > revision);
    }

    [Fact]
    public async Task PublishedScreenRequiresRepublishBeforeTagCanBeDeleted()
    {
        var admin = await _site.ClientAsync("admin");
        var tagA = await CreateTagAsync(admin, "发布反查");
        var tagB = await CreateTagAsync(admin, "发布目标");
        var (screenId, revision) = await CreateScreenAsync(admin, Document(Node("n1", "value", tagA)), "发布反查画面");
        var published = await Ok(await admin.PostAsJsonAsync($"/api/screens/{screenId}/publish", new { revision }));
        Assert.Equal(published.GetProperty("revision").GetInt32(), published.GetProperty("publishedRevision").GetInt32());

        var rebound = await Ok(await admin.PostAsJsonAsync($"/api/tags/{tagA}/rebind", new { targetTagId = tagB }));
        Assert.Equal(1, rebound.GetProperty("screens").GetInt32());
        var pending = rebound.GetProperty("publishedScreens").EnumerateArray().Single();
        Assert.Equal(screenId, pending.GetProperty("id").GetInt32());
        Assert.True(pending.GetProperty("requiresRepublish").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/tags/{tagA}")).StatusCode);
        var detail = await Ok(await admin.GetAsync($"/api/screens/{screenId}?draft=true"));
        var republished = await Ok(await admin.PostAsJsonAsync($"/api/screens/{screenId}/publish", new { revision = detail.GetProperty("revision").GetInt32() }));
        Assert.Equal(republished.GetProperty("revision").GetInt32(), republished.GetProperty("publishedRevision").GetInt32());
        await Ok(await admin.DeleteAsync($"/api/tags/{tagA}"));
    }

    [Fact]
    public async Task RebindUpdatesAlarmRulesSequencesAndRecipes()
    {
        var admin = await _site.ClientAsync("admin");
        var source = await CreateTagAsync(admin, "规则原");
        var target = await CreateTagAsync(admin, "规则目标");
        var suffix = U();
        var rule = await Ok(await admin.PostAsJsonAsync("/api/alarm-rules", new
        {
            tagId = source, type = "High", threshold = 10, level = "Warning", delaySec = 0, recoverDeadband = 0,
            enabled = true, message = "规则" + suffix
        }));
        var sequence = await Ok(await admin.PostAsJsonAsync("/api/control/sequences", new
        {
            name = "顺控" + suffix, areaId = _site.AreaA, dualApproval = false,
            steps = new[] { new { name = "写入", tagId = source, value = 1.0, timeoutMs = 500, delayMs = 0 } }
        }));
        var recipe = await Ok(await admin.PostAsJsonAsync("/api/recipes/templates", new
        {
            name = "配方" + suffix, areaId = _site.AreaA, dualApproval = false,
            parameters = new[] { new { key = "p", name = "参数", tagId = source, min = 0, max = 100 } }
        }));

        var usage = await Ok(await admin.GetAsync($"/api/tags/{source}/usage"));
        var categories = usage.GetProperty("references").EnumerateArray().Select(item => item.GetProperty("category").GetString()).ToHashSet();
        Assert.Contains("alarmRule", categories);
        Assert.Contains("sequence", categories);
        Assert.Contains("recipe", categories);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/tags/{source}")).StatusCode);

        var rebound = await Ok(await admin.PostAsJsonAsync($"/api/tags/{source}/rebind", new { targetTagId = target }));
        Assert.Equal(1, rebound.GetProperty("alarmRules").GetInt32());
        Assert.Equal(1, rebound.GetProperty("sequences").GetInt32());
        Assert.Equal(1, rebound.GetProperty("recipes").GetInt32());
        var after = await Ok(await admin.GetAsync($"/api/tags/{source}/usage"));
        Assert.Empty(after.GetProperty("references").EnumerateArray());
        await Ok(await admin.DeleteAsync($"/api/tags/{source}"));

        // 目标测点继续被三类配置引用，说明替换后的配置仍然有效。
        var targetUsage = await Ok(await admin.GetAsync($"/api/tags/{target}/usage"));
        Assert.Equal(3, targetUsage.GetProperty("references").GetArrayLength());
        Assert.Equal(rule.GetProperty("id").GetInt32(), targetUsage.GetProperty("references").EnumerateArray().Single(item => item.GetProperty("category").GetString() == "alarmRule").GetProperty("id").GetInt32());
        Assert.Equal(sequence.GetProperty("id").GetInt32(), targetUsage.GetProperty("references").EnumerateArray().Single(item => item.GetProperty("category").GetString() == "sequence").GetProperty("id").GetInt32());
        Assert.Equal(recipe.GetProperty("id").GetInt32(), targetUsage.GetProperty("references").EnumerateArray().Single(item => item.GetProperty("category").GetString() == "recipe").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ThumbnailIsCompactAndExternalImagesRequireOptIn()
    {
        var admin = await _site.ClientAsync("admin");
        var name = "缩略图" + U();
        var created = await Ok(await admin.PostAsJsonAsync("/api/screens", new { name }));
        var id = created.GetProperty("id").GetInt32();
        object[] nodes =
        [
            new { id = "text", type = "text", x = 10, y = 10, width = 200, height = 60, text = "hello" },
            new { id = "img", type = "image", x = 10, y = 100, width = 200, height = 120, text = "https://example.com/a.png" }
        ];
        var saved = await Ok(await admin.PutAsJsonAsync($"/api/screens/{id}", new
        {
            name, revision = created.GetProperty("revision").GetInt32(),
            document = new { schemaVersion = 1, width = 1200, height = 720, background = "#101b2d", nodes }
        }));

        var thumbnail = await Ok(await admin.GetAsync($"/api/screens/{id}/thumbnail?draft=true"));
        Assert.Equal(1200, thumbnail.GetProperty("width").GetDouble());
        Assert.False(thumbnail.GetProperty("truncated").GetBoolean());
        var thumbnailNodes = thumbnail.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(2, thumbnailNodes.Count);
        Assert.True(thumbnailNodes.Single(item => item.GetProperty("id").GetString() == "img").GetProperty("text").ValueKind is JsonValueKind.Null);

        var blocked = await admin.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision = saved.GetProperty("revision").GetInt32() });
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("外部地址", await blocked.Content.ReadAsStringAsync());

        var optedIn = await Ok(await admin.PutAsJsonAsync($"/api/screens/{id}", new
        {
            name, revision = saved.GetProperty("revision").GetInt32(),
            document = new { schemaVersion = 1, width = 1200, height = 720, background = "#101b2d", allowExternalImages = true, nodes }
        }));
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision = optedIn.GetProperty("revision").GetInt32() }));
    }

    [Fact]
    public async Task PublishedScreenCatalogCanBeFilteredByVisibleArea()
    {
        var admin = await _site.ClientAsync("admin");
        async Task PublishAsync(string name, int? areaId)
        {
            var created = await Ok(await admin.PostAsJsonAsync("/api/screens", new { name, areaId }));
            await Ok(await admin.PostAsJsonAsync($"/api/screens/{created.GetProperty("id").GetInt32()}/publish", new { revision = created.GetProperty("revision").GetInt32() }));
        }
        var visibleName = "可见A" + U();
        var hiddenName = "可见B" + U();
        var globalName = "跨区域" + U();
        await PublishAsync(visibleName, _site.AreaA);
        await PublishAsync(hiddenName, _site.AreaB);
        await PublishAsync(globalName, null);

        var op = await _site.ClientAsync("opA");
        var settings = _site.Services.GetRequiredService<IOptions<ScadaSettings>>().Value;
        var original = settings.FilterPublishedScreensByArea;
        try
        {
            settings.FilterPublishedScreensByArea = false;
            var all = (await Ok(await op.GetAsync("/api/screens"))).EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToHashSet();
            Assert.Contains(visibleName, all);
            Assert.Contains(hiddenName, all);
            Assert.Contains(globalName, all);

            settings.FilterPublishedScreensByArea = true;
            var filtered = (await Ok(await op.GetAsync("/api/screens"))).EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToHashSet();
            Assert.Contains(visibleName, filtered);
            Assert.DoesNotContain(hiddenName, filtered);
            Assert.Contains(globalName, filtered);
        }
        finally { settings.FilterPublishedScreensByArea = original; }
    }

    [Fact]
    public async Task RuntimeReportsFeedHealthWarningsAndAuditOncePerThrottleWindow()
    {
        var admin = await _site.ClientAsync("admin");
        var name = "运行上报" + U();
        var created = await Ok(await admin.PostAsJsonAsync("/api/screens", new { name }));
        var id = created.GetProperty("id").GetInt32();
        await Ok(await admin.PostAsJsonAsync($"/api/screens/{id}/publish", new { revision = created.GetProperty("revision").GetInt32() }));

        // 类型白名单与内容长度在入口处拦截，避免任意文本写进健康摘要和审计。
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/screens/{id}/runtime-report", new { kind = "unknown" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync($"/api/screens/{id}/runtime-report", new { kind = "loadFailed", message = new string('x', 501) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/api/screens/999999/runtime-report", new { kind = "loadFailed" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/screens/{id}/runtime-report", new { kind = "loadFailed", message = "网络中断", publishedRevision = 1 })).StatusCode);
        // 30 秒节流窗口内重复上报只累加次数，不再重复写审计。
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostAsJsonAsync($"/api/screens/{id}/runtime-report", new { kind = "loadFailed", message = "网络中断" })).StatusCode);

        var health = _site.Services.GetRequiredService<ScreenRuntimeHealth>();
        Assert.Contains(health.HealthWarnings(), warning => warning.Code == "screen_runtime_error" && warning.Message.Contains(name));
        // 健康聚合通过 IHealthContributor 自动收集，无需在 /api/health 里单独接线。
        var snapshot = await Ok(await admin.GetAsync("/api/health"));
        Assert.Contains(snapshot.GetProperty("warnings").EnumerateArray(), item => item.GetProperty("code").GetString() == "screen_runtime_error");

        var runtime = _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();
        await using var db = await runtime.CreateDbContextAsync();
        var logs = await db.OperationLogs
            .Where(log => log.TargetType == "ScreenRuntime" && log.TargetId == id.ToString())
            .ToListAsync();
        Assert.Single(logs);
        Assert.Contains("运行端异常", logs[0].Action);
    }

    [Fact]
    public async Task PublishWarnsForMissingOeeLineAndBlocksMissingSequence()
    {
        var admin = await _site.ClientAsync("admin");
        var oee = await CreateScreenAsync(admin,
            Document(new { id = "oee", type = "oee-kpi", x = 10, y = 10, width = 300, height = 200, lineId = 999999 }), "OEE 引用");
        var validation = await Ok(await admin.PostAsJsonAsync($"/api/screens/validate?screenId={oee.Id}", Document(new { id = "oee", type = "oee-kpi", x = 10, y = 10, width = 300, height = 200, lineId = 999999 })));
        Assert.Empty(validation.GetProperty("issues").EnumerateArray());
        Assert.Contains(validation.GetProperty("warnings").EnumerateArray(), item => item.GetProperty("message").GetString()!.Contains("OEE 产线"));
        var published = await Ok(await admin.PostAsJsonAsync($"/api/screens/{oee.Id}/publish", new { revision = oee.Revision }));
        Assert.NotEmpty(published.GetProperty("warnings").EnumerateArray());

        var sequence = await CreateScreenAsync(admin,
            Document(new { id = "run", type = "button", x = 10, y = 10, width = 120, height = 48, action = new { type = "sequence", sequenceId = 999999, screenId = (int?)null, tagId = (int?)null, value = 1 } }), "顺控引用");
        var rejected = await admin.PostAsJsonAsync($"/api/screens/{sequence.Id}/publish", new { revision = sequence.Revision });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("顺控流程", await rejected.Content.ReadAsStringAsync());
    }
}
