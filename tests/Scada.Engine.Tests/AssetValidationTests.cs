using Scada.Core.Entities;
using Scada.Core.Models;

namespace Scada.Engine.Tests;

public class AssetValidationTests
{
    private static Asset ValidAsset() => new()
    {
        Code = "P-001", Name = "1号泵", Category = "泵", AreaId = 1, Status = AssetStatus.InService
    };

    [Fact]
    public void AssetAcceptsValidMasterData() => Assert.Null(AssetValidation.Asset(ValidAsset()));

    [Theory]
    [InlineData("", "1号泵")]
    [InlineData("P-001", "")]
    public void AssetRequiresCodeAndName(string code, string name)
    {
        var asset = ValidAsset();
        asset.Code = code;
        asset.Name = name;
        Assert.NotNull(AssetValidation.Asset(asset));
    }

    [Fact]
    public void AssetRejectsWarrantyBeforeCommissioning()
    {
        var asset = ValidAsset();
        asset.CommissionedTs = 2_000;
        asset.WarrantyUntilTs = 1_000;
        Assert.NotNull(AssetValidation.Asset(asset));
    }

    [Fact]
    public void AssetRejectsUnknownStatus()
    {
        var asset = ValidAsset();
        asset.Status = (AssetStatus)99;
        Assert.NotNull(AssetValidation.Asset(asset));
    }

    [Fact]
    public void AssetRejectsOverlongDescription()
    {
        var asset = ValidAsset();
        asset.Description = new string('x', 501);
        Assert.NotNull(AssetValidation.Asset(asset));
    }

    private static MaintenanceWorkOrder ValidWorkOrder() => new()
    {
        Number = "WO-20261107-ABC123", Title = "更换密封", AreaId = 1,
        Type = WorkOrderType.Corrective, Priority = WorkOrderPriority.High, Status = WorkOrderStatus.Open
    };

    [Fact]
    public void WorkOrderAcceptsValidData() => Assert.Null(AssetValidation.WorkOrder(ValidWorkOrder()));

    [Fact]
    public void WorkOrderRequiresTitle()
    {
        var order = ValidWorkOrder();
        order.Title = " ";
        Assert.NotNull(AssetValidation.WorkOrder(order));
    }

    [Fact]
    public void WorkOrderRejectsPlanEndBeforeStart()
    {
        var order = ValidWorkOrder();
        order.PlannedStartTs = 2_000;
        order.PlannedEndTs = 1_000;
        Assert.NotNull(AssetValidation.WorkOrder(order));
    }

    [Fact]
    public void WorkOrderRejectsNegativeDowntime()
    {
        var order = ValidWorkOrder();
        order.DowntimeMinutes = -1;
        Assert.NotNull(AssetValidation.WorkOrder(order));
    }

    [Fact]
    public void WorkOrderRejectsUnknownStatus()
    {
        var order = ValidWorkOrder();
        order.Status = (WorkOrderStatus)99;
        Assert.NotNull(AssetValidation.WorkOrder(order));
    }

    [Fact]
    public void WorkOrderRejectsOverlongResolution()
    {
        var order = ValidWorkOrder();
        order.Resolution = new string('x', 2001);
        Assert.NotNull(AssetValidation.WorkOrder(order));
    }

    private static SparePart ValidPart() => new() { Code = "SP-001", Name = "密封圈", Unit = "件", Stock = 3, MinStock = 1 };

    [Fact]
    public void SparePartAcceptsValidData() => Assert.Null(AssetValidation.SparePart(ValidPart()));

    [Fact]
    public void SparePartRejectsNegativeStock()
    {
        var part = ValidPart();
        part.Stock = -1;
        Assert.NotNull(AssetValidation.SparePart(part));
    }

    [Fact]
    public void SparePartRejectsNonFiniteStock()
    {
        var part = ValidPart();
        part.Stock = double.NaN;
        Assert.NotNull(AssetValidation.SparePart(part));
    }

    [Fact]
    public void SparePartRejectsNegativeMinimumStock()
    {
        var part = ValidPart();
        part.MinStock = -1;
        Assert.NotNull(AssetValidation.SparePart(part));
    }

    [Fact]
    public void SparePartRejectsNegativeUnitPrice()
    {
        var part = ValidPart();
        part.UnitPrice = -1;
        Assert.NotNull(AssetValidation.SparePart(part));
    }

    [Fact]
    public void SparePartRejectsOverlongUnit()
    {
        var part = ValidPart();
        part.Unit = new string('x', 33);
        Assert.NotNull(AssetValidation.SparePart(part));
    }

    [Fact]
    public void MovementAcceptsStockIn()
        => Assert.Null(AssetValidation.Movement(SparePartMovementType.In, 1, ""));

    [Fact]
    public void MovementRejectsZeroStockIn()
        => Assert.NotNull(AssetValidation.Movement(SparePartMovementType.In, 0, ""));

    [Fact]
    public void MovementRejectsNegativeStockOut()
        => Assert.NotNull(AssetValidation.Movement(SparePartMovementType.Out, -1, ""));

    [Fact]
    public void MovementAcceptsZeroCountBalance()
        => Assert.Null(AssetValidation.Movement(SparePartMovementType.Adjust, 0, ""));

    [Fact]
    public void MovementRejectsNegativeCountBalance()
        => Assert.NotNull(AssetValidation.Movement(SparePartMovementType.Adjust, -1, ""));

    [Fact]
    public void MovementRejectsOverlongNote()
        => Assert.NotNull(AssetValidation.Movement(SparePartMovementType.In, 1, new string('x', 501)));

    [Fact]
    public void WorkOrderNumberUsesDayAndSixHexCharacters()
    {
        var ts = DateTimeOffset.Parse("2026-11-07T00:00:00+08:00").ToUnixTimeMilliseconds();
        var number = AssetValidation.NewWorkOrderNumber(ts);
        Assert.Matches(@"^WO-\d{8}-[0-9A-F]{6}$", number);
    }
}
