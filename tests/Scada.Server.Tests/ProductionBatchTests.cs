using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Engine.Control;

namespace Scada.Server.Tests;

public sealed class ProductionBatchTests : IClassFixture<ControlTests.ControlSite>
{
    private readonly ControlTests.ControlSite _site;
    public ProductionBatchTests(ControlTests.ControlSite site) => _site = site;
    private ControlTests.FakeCollector Collector => _site.Services.GetRequiredService<ControlTests.FakeCollector>();
    private void Set(int tagId, double value) => _site.Services.GetRequiredService<IRealtimeCache>().Set(new TagValue(tagId, value, TagQuality.Good, ScadaTime.NowMs()));
    private static string U() => Guid.NewGuid().ToString("N")[..5];
    private IDbContextFactory<RuntimeDbContext> Runtime => _site.Services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>();

    private static async Task<JsonElement> Ok(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return r.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.Accepted ? default : await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> TagAsync(HttpClient admin, string name)
    {
        var payload = new
        {
            deviceId = _site.DeviceA,
            name = name + U(),
            area = "HoldingRegister",
            address = Random.Shared.Next(2000, 30000) * 2,
            dataType = "Float",
            scale = 1.0,
            offset = 0.0,
            writable = true,
            archiveMode = "None",
            deadband = 0.0,
            archivePeriodSec = 600
        };
        return (await Ok(await admin.PostAsJsonAsync("/api/tags", payload))).GetProperty("id").GetInt32();
    }

    private async Task<(int Recipe, int[] Tags)> SetupRecipeAsync(HttpClient admin)
    {
        var tags = new[] { await TagAsync(admin, "批温"), await TagAsync(admin, "批压") };
        foreach (var t in tags) Set(t, 1);
        var template = await Ok(await admin.PostAsJsonAsync("/api/recipes/templates", new
        {
            name = "批次模板" + U(),
            areaId = _site.AreaA,
            dualApproval = false,
            parameters = new object[]
            {
                new { key = "temp", name = "温度", tagId = tags[0], unit = "℃", min = 0, max = 200 },
                new { key = "press", name = "压力", tagId = tags[1], unit = "kPa", min = 0, max = 100 }
            }
        }));
        var recipe = await Ok(await admin.PostAsJsonAsync("/api/recipes", new
        {
            templateId = template.GetProperty("id").GetInt32(),
            name = "批产品" + U(),
            values = new[] { new { key = "temp", value = 80 }, new { key = "press", value = 20 } },
            note = "初版"
        }));
        return (recipe.GetProperty("recipe").GetProperty("id").GetInt32(), tags);
    }

    private async Task<int> SetupSequenceAsync(HttpClient admin, int tagId, double value = 5)
    {
        var seq = await Ok(await admin.PostAsJsonAsync("/api/control/sequences", new
        {
            name = "批次顺控" + U(),
            areaId = _site.AreaA,
            dualApproval = false,
            steps = new[] { new { name = "写入", tagId, value, timeoutMs = 2000, delayMs = 0 } }
        }));
        return seq.GetProperty("id").GetInt32();
    }

    private static async Task<HttpResponseMessage> StartBatchAsync(HttpClient client, string number, int recipeId, int version, int? sequenceId, bool confirmed = true, bool monitorOnly = false)
    {
        string? challenge = null;
        var check = await client.PostAsJsonAsync("/api/production-batches/check", new { recipeId, version, sequenceId });
        if (check.IsSuccessStatusCode)
            challenge = (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("confirmationChallenge").GetString();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/production-batches")
        {
            Content = JsonContent.Create(new { batchNumber = number, recipeId, version, sequenceId, confirmed, challenge })
        };
        if (monitorOnly) req.Headers.Add("X-Scada-Monitor-Only", "1");
        return await client.SendAsync(req);
    }

    private async Task<JsonElement> WaitBatchAsync(HttpClient c, int id)
    {
        await _site.Services.GetRequiredService<ProductionBatchService>().WaitAsync(id);
        return await Ok(await c.GetAsync($"/api/production-batches/{id}"));
    }

    [Fact]
    public async Task StartBatchDownloadsRecipeAndCompletesWithQueryableRecord()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA"); var cfg = await _site.ClientAsync("cfgA");
        var (rid, tags) = await SetupRecipeAsync(admin);
        var number = "BN-" + U();
        var missingRecipe = await op.PostAsJsonAsync("/api/production-batches", new { batchNumber = number, recipeId = 0, version = 0, confirmed = true });
        Assert.Equal(HttpStatusCode.BadRequest, missingRecipe.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartBatchAsync(cfg, number, rid, 0, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await StartBatchAsync(op, number + "m", rid, 0, null, monitorOnly: true)).StatusCode);

        var started = await Ok(await StartBatchAsync(op, number, rid, 0, null));
        var id = started.GetProperty("id").GetInt32();
        var detail = await WaitBatchAsync(op, id);
        var batch = detail.GetProperty("batch");
        Assert.Equal("Completed", batch.GetProperty("status").GetString());
        Assert.Equal(number, batch.GetProperty("batchNumber").GetString());
        var results = detail.GetProperty("download").GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.All(results.EnumerateArray(), r =>
        {
            Assert.Equal("done", r.GetProperty("status").GetString());
            Assert.Equal(r.GetProperty("target").GetDouble(), r.GetProperty("readBack").GetDouble());
        });
        Assert.True(detail.GetProperty("audits").GetArrayLength() > 0);
        Assert.Null(detail.GetProperty("sequenceRun").ValueKind == JsonValueKind.Null ? (int?)null : 0);

        var list = await Ok(await op.GetAsync($"/api/production-batches?batchNumber={number}"));
        Assert.Contains(list.EnumerateArray(), x => x.GetProperty("id").GetInt32() == id);
        Assert.All(tags, t => Assert.True(Collector.Count(t) > 0));
    }

    [Fact]
    public async Task RecipeDownloadFailureFailsBatchAndDoesNotStartSequence()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var (rid, tags) = await SetupRecipeAsync(admin);
        var seqId = await SetupSequenceAsync(admin, tags[0]);
        lock (Collector.Writes) Collector.Fail.Add(tags[0]);
        try
        {
            var number = "BN-fail-" + U();
            var started = await Ok(await StartBatchAsync(op, number, rid, 0, seqId));
            var detail = await WaitBatchAsync(op, started.GetProperty("id").GetInt32());
            Assert.Equal("Failed", detail.GetProperty("batch").GetProperty("status").GetString());
            Assert.Contains("未启动顺控", detail.GetProperty("batch").GetProperty("message").GetString());
            Assert.True(detail.GetProperty("sequenceRun").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
            Assert.Null(detail.GetProperty("batch").GetProperty("controlSequenceRunId").ValueKind == JsonValueKind.Null ? (int?)null : detail.GetProperty("batch").GetProperty("controlSequenceRunId").GetInt32());
            await using var runtime = await Runtime.CreateDbContextAsync();
            Assert.False(await runtime.ControlSequenceRuns.AnyAsync(x => x.SequenceId == seqId));
        }
        finally { lock (Collector.Writes) Collector.Fail.Remove(tags[0]); }
    }

    [Fact]
    public async Task StartBatchMayRunSequenceAndAbortPendingDownload()
    {
        var admin = await _site.ClientAsync("admin"); var op = await _site.ClientAsync("opA");
        var (rid, tags) = await SetupRecipeAsync(admin);
        var seqId = await SetupSequenceAsync(admin, tags[0], 9);
        var number = "BN-seq-" + U();
        var started = await Ok(await StartBatchAsync(op, number, rid, 0, seqId));
        var detail = await WaitBatchAsync(op, started.GetProperty("id").GetInt32());
        Assert.Equal("Completed", detail.GetProperty("batch").GetProperty("status").GetString());
        Assert.Equal("Completed", detail.GetProperty("download").GetProperty("status").GetString());
        var steps = detail.GetProperty("sequenceRun").GetProperty("steps");
        Assert.True(steps.GetArrayLength() >= 1);
        Assert.Equal("done", steps[0].GetProperty("status").GetString());

        // 双人复核下发未批准时中止批次：不能自动开顺控，下发应被驳回。
        var dual = await Ok(await admin.PostAsJsonAsync("/api/recipes/templates", new
        {
            name = "复核模板" + U(), areaId = _site.AreaA, dualApproval = true,
            parameters = new object[] { new { key = "temp", name = "温度", tagId = tags[0], unit = "℃", min = 0, max = 200 } }
        }));
        var recipe = await Ok(await admin.PostAsJsonAsync("/api/recipes", new
        {
            templateId = dual.GetProperty("id").GetInt32(), name = "待复核" + U(),
            values = new[] { new { key = "temp", value = 70.0 } }
        }));
        var pendingNumber = "BN-abort-" + U();
        var pending = await Ok(await StartBatchAsync(op, pendingNumber, recipe.GetProperty("recipe").GetProperty("id").GetInt32(), 0, seqId));
        Assert.Equal("Starting", pending.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Accepted, (await op.PostAsync($"/api/production-batches/{pending.GetProperty("id").GetInt32()}/abort", null)).StatusCode);
        var aborted = await Ok(await op.GetAsync($"/api/production-batches/{pending.GetProperty("id").GetInt32()}"));
        Assert.Equal("Aborted", aborted.GetProperty("batch").GetProperty("status").GetString());
        var download = aborted.GetProperty("download");
        if (download.ValueKind != JsonValueKind.Null)
            Assert.NotEqual("Completed", download.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TakeoverMarksOrphanBatchNeedsConfirmationWithoutStartingSequence()
    {
        var admin = await _site.ClientAsync("admin");
        var (rid, tags) = await SetupRecipeAsync(admin);
        var seqId = await SetupSequenceAsync(admin, tags[0]);
        var number = "BN-orphan-" + U();
        await using (var runtime = await Runtime.CreateDbContextAsync())
        {
            runtime.ProductionBatches.Add(new ProductionBatch
            {
                BatchNumber = number, AreaId = _site.AreaA, RecipeId = rid, RecipeName = "孤儿", RecipeVersion = 1,
                SequenceId = seqId, SequenceName = "未跑", Status = ProductionBatchStatus.Starting,
                StartedTs = ScadaTime.NowMs(), StartedBy = "admin"
            });
            await runtime.SaveChangesAsync();
        }
        await _site.Services.GetRequiredService<ProductionBatchService>().ReconcileOrphanBatchesAsync(true, CancellationToken.None);
        var list = await Ok(await admin.GetAsync($"/api/production-batches?batchNumber={number}"));
        var row = list.EnumerateArray().Single(x => x.GetProperty("batchNumber").GetString() == number);
        Assert.Equal("NeedsConfirmation", row.GetProperty("status").GetString());
        Assert.Contains("不会自动续跑", row.GetProperty("message").GetString());
        Assert.True(row.GetProperty("controlSequenceRunId").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined);
        await using var check = await Runtime.CreateDbContextAsync();
        Assert.False(await check.ControlSequenceRuns.AnyAsync(x => x.SequenceId == seqId));
    }
}
