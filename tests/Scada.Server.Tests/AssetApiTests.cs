using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Scada.Core.Entities;
using Scada.Data;

namespace Scada.Server.Tests;

public sealed class AssetApiTests : IClassFixture<AuthorizationTests.Site>
{
    private readonly AuthorizationTests.Site _site;
    public AssetApiTests(AuthorizationTests.Site site) => _site = site;
    private IDbContextFactory<ConfigDbContext> Factory() => _site.Services.GetRequiredService<IDbContextFactory<ConfigDbContext>>();
    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private static object AssetBody(string code, int areaId) => new
    {
        code, name = "测试资产-" + code, category = "泵", model = "M-100", manufacturer = "测试厂商",
        serialNumber = "SN-" + code, location = "A区", areaId, deviceId = (int?)null, status = "InService",
        commissionedTs = (long?)null, warrantyUntilTs = (long?)null, description = "", enabled = true
    };

    private static async Task<int> CreateAsset(HttpClient client, string code, int areaId)
    {
        var created = await Json(await client.PostAsJsonAsync("/api/assets", AssetBody(code, areaId)));
        return created.GetProperty("id").GetInt32();
    }

    private static object WorkOrderBody(string title, int? areaId, int? assetId) => new
    {
        title, assetId, deviceId = (int?)null, areaId, type = "Corrective", priority = "High",
        description = "测试工单", assignee = "opA", reporter = "opA",
        plannedStartTs = (long?)null, plannedEndTs = (long?)null
    };

    private static object PartBody(string code, double stock, double minStock) => new
    {
        code, name = "备件-" + code, specification = "10mm", unit = "件", stock, minStock,
        location = "备件库", supplier = "测试供应商", unitPrice = 12.5, description = "", enabled = true
    };

    [Fact]
    public async Task AssetAndWorkOrderPermissionsFollowArea()
    {
        var opA = await _site.ClientAsync("opA");
        var cfgA = await _site.ClientAsync("cfgA");
        var admin = await _site.ClientAsync("admin");

        // 资产主数据需要区域内 Configure；Control 不能替代。
        var codeA = Unique("AST-A-");
        Assert.Equal(HttpStatusCode.Forbidden, (await opA.PostAsJsonAsync("/api/assets", AssetBody(codeA, _site.AreaA))).StatusCode);
        var assetA = await CreateAsset(cfgA, codeA, _site.AreaA);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfgA.PostAsJsonAsync("/api/assets", AssetBody(Unique("AST-B-"), _site.AreaB))).StatusCode);
        var codeB = Unique("AST-B-");
        var assetB = await CreateAsset(admin, codeB, _site.AreaB);

        var listOp = await Json(await opA.GetAsync("/api/assets"));
        Assert.Contains(listOp.EnumerateArray(), x => x.GetProperty("id").GetInt32() == assetA);
        Assert.DoesNotContain(listOp.EnumerateArray(), x => x.GetProperty("id").GetInt32() == assetB);
        Assert.Equal(HttpStatusCode.Forbidden, (await cfgA.PutAsJsonAsync($"/api/assets/{assetB}", AssetBody(codeB, _site.AreaB))).StatusCode);

        // 工单创建需要区域内 Control；Configure 单独不能建单。
        Assert.Equal(HttpStatusCode.Forbidden, (await cfgA.PostAsJsonAsync("/api/work-orders", WorkOrderBody("配置员不能建单", null, assetA))).StatusCode);
        var orderA = await Json(await opA.PostAsJsonAsync("/api/work-orders", WorkOrderBody("A区维修", null, assetA)));
        var orderAId = orderA.GetProperty("id").GetInt32();
        Assert.Equal(_site.AreaA, orderA.GetProperty("areaId").GetInt32());
        Assert.StartsWith("WO-", orderA.GetProperty("number").GetString());
        Assert.Equal("Open", orderA.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await opA.PostAsJsonAsync("/api/work-orders", WorkOrderBody("越权B区", _site.AreaB, null))).StatusCode);
        var orderB = await Json(await admin.PostAsJsonAsync("/api/work-orders", WorkOrderBody("B区维修", _site.AreaB, null)));
        var orderBId = orderB.GetProperty("id").GetInt32();

        var opOrders = await Json(await opA.GetAsync("/api/work-orders"));
        Assert.Contains(opOrders.EnumerateArray(), x => x.GetProperty("id").GetInt32() == orderAId);
        Assert.DoesNotContain(opOrders.EnumerateArray(), x => x.GetProperty("id").GetInt32() == orderBId);
        var cfgOrders = await Json(await cfgA.GetAsync("/api/work-orders"));
        Assert.DoesNotContain(cfgOrders.EnumerateArray(), x => x.GetProperty("id").GetInt32() == orderBId);
    }

    [Fact]
    public async Task WorkOrderLifecycleIssuesPartsAndSyncsAssetStatus()
    {
        var opA = await _site.ClientAsync("opA");
        var cfgA = await _site.ClientAsync("cfgA");
        var assetCode = Unique("AST-");
        var assetId = await CreateAsset(cfgA, assetCode, _site.AreaA);

        var partCode = Unique("SP-");
        Assert.Equal(HttpStatusCode.Forbidden, (await opA.PostAsJsonAsync("/api/spare-parts", PartBody(partCode, 10, 2))).StatusCode);
        var part = await Json(await cfgA.PostAsJsonAsync("/api/spare-parts", PartBody(partCode, 10, 2)));
        var partId = part.GetProperty("id").GetInt32();

        var order = await Json(await opA.PostAsJsonAsync("/api/work-orders", WorkOrderBody("更换密封", null, assetId)));
        var orderId = order.GetProperty("id").GetInt32();

        var started = await Json(await opA.PostAsJsonAsync($"/api/work-orders/{orderId}/status", new { status = "InProgress" }));
        Assert.Equal("InProgress", started.GetProperty("status").GetString());
        await using (var db = await Factory().CreateDbContextAsync())
            Assert.Equal(AssetStatus.Maintenance, (await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId)).Status);

        var issued = await Json(await opA.PostAsJsonAsync($"/api/work-orders/{orderId}/parts", new { partId, quantity = 2.0, note = "更换密封" }));
        Assert.Equal(8.0, issued.GetProperty("stock").GetDouble());
        var movements = await Json(await opA.GetAsync($"/api/work-orders/{orderId}/parts"));
        var single = Assert.Single(movements.EnumerateArray().ToArray());
        Assert.Equal("Out", single.GetProperty("type").GetString());
        Assert.Equal(2.0, single.GetProperty("quantity").GetDouble());
        Assert.Equal(8.0, single.GetProperty("balanceAfter").GetDouble());

        Assert.Equal(HttpStatusCode.BadRequest, (await opA.PostAsJsonAsync($"/api/work-orders/{orderId}/parts", new { partId, quantity = 9.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await opA.PostAsJsonAsync($"/api/work-orders/{orderId}/status", new { status = "Completed" })).StatusCode);
        var completed = await Json(await opA.PostAsJsonAsync($"/api/work-orders/{orderId}/status", new
        {
            status = "Completed", resolution = "已更换密封并试运行", downtimeMinutes = 35
        }));
        Assert.Equal("Completed", completed.GetProperty("status").GetString());
        Assert.Equal(35, completed.GetProperty("downtimeMinutes").GetInt32());
        await using (var db = await Factory().CreateDbContextAsync())
            Assert.Equal(AssetStatus.InService, (await db.Assets.AsNoTracking().SingleAsync(x => x.Id == assetId)).Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await opA.PostAsJsonAsync($"/api/work-orders/{orderId}/parts", new { partId, quantity = 1.0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cfgA.PutAsJsonAsync($"/api/work-orders/{orderId}", WorkOrderBody("完成后修改", null, assetId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cfgA.DeleteAsync($"/api/work-orders/{orderId}")).StatusCode);

        var cancellable = await Json(await opA.PostAsJsonAsync("/api/work-orders", WorkOrderBody("待取消", null, assetId)));
        var cancellableId = cancellable.GetProperty("id").GetInt32();
        await Json(await opA.PostAsJsonAsync($"/api/work-orders/{cancellableId}/status", new { status = "Cancelled" }));
        Assert.Equal(HttpStatusCode.NoContent, (await cfgA.DeleteAsync($"/api/work-orders/{cancellableId}")).StatusCode);

        // 库存流水：入库、盘点调整和出库数量校验；有流水的备件不能删除。
        var inbound = await Json(await opA.PostAsJsonAsync($"/api/spare-parts/{partId}/movements", new { type = "In", quantity = 3.0, note = "采购入库" }));
        Assert.Equal(11.0, inbound.GetProperty("stock").GetDouble());
        Assert.Equal(HttpStatusCode.BadRequest, (await opA.PostAsJsonAsync($"/api/spare-parts/{partId}/movements", new { type = "Out", quantity = 20.0 })).StatusCode);
        var adjusted = await Json(await opA.PostAsJsonAsync($"/api/spare-parts/{partId}/movements", new { type = "Adjust", quantity = 4.0, note = "月度盘点" }));
        Assert.Equal(4.0, adjusted.GetProperty("stock").GetDouble());
        Assert.Equal(HttpStatusCode.BadRequest, (await opA.PostAsJsonAsync($"/api/spare-parts/{partId}/movements", new { type = "In", quantity = 0.0 })).StatusCode);
        var history = await Json(await opA.GetAsync($"/api/spare-parts/{partId}/movements"));
        Assert.Equal(3, history.GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await cfgA.DeleteAsync($"/api/spare-parts/{partId}")).StatusCode);

        var removable = await Json(await cfgA.PostAsJsonAsync("/api/spare-parts", PartBody(Unique("SP-NEW-"), 1, 0)));
        var removableId = removable.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await cfgA.DeleteAsync($"/api/spare-parts/{removableId}")).StatusCode);
    }
}
