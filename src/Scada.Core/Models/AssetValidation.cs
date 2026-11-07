using Scada.Core.Entities;
using AssetEntity = Scada.Core.Entities.Asset;
using WorkOrderEntity = Scada.Core.Entities.MaintenanceWorkOrder;
using SparePartEntity = Scada.Core.Entities.SparePart;

namespace Scada.Core.Models;

public static class AssetValidation
{
    public static string? Asset(AssetEntity asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Code) || asset.Code.Trim().Length > 64) return "资产编号须为 1～64 个字符";
        if (string.IsNullOrWhiteSpace(asset.Name) || asset.Name.Trim().Length > 100) return "资产名称须为 1～100 个字符";
        if (asset.Category.Length > 64) return "资产分类过长（最多 64 个字符）";
        if (asset.Model.Length > 128 || asset.Manufacturer.Length > 128 || asset.SerialNumber.Length > 128 || asset.Location.Length > 128)
            return "型号、厂商、序列号或位置过长（最多 128 个字符）";
        if (asset.AreaId <= 0) return "必须指定区域";
        if (!Enum.IsDefined(asset.Status)) return "资产状态无效";
        if (asset.Description.Length > 500) return "资产描述过长（最多 500 个字符）";
        if (asset.CommissionedTs is long commissioned && asset.WarrantyUntilTs is long warranty && warranty < commissioned)
            return "质保截止时间不能早于投运时间";
        return null;
    }

    public static string? WorkOrder(WorkOrderEntity order)
    {
        if (string.IsNullOrWhiteSpace(order.Title) || order.Title.Trim().Length > 200) return "工单标题须为 1～200 个字符";
        if (order.Number.Length > 40) return "工单编号过长（最多 40 个字符）";
        if (order.AreaId <= 0) return "必须指定区域";
        if (!Enum.IsDefined(order.Type) || !Enum.IsDefined(order.Priority) || !Enum.IsDefined(order.Status)) return "工单类型、优先级或状态无效";
        if (order.Description.Length > 2000 || order.Resolution.Length > 2000) return "工单描述或处理结果过长（最多 2000 个字符）";
        if (order.Assignee.Length > 100 || order.Reporter.Length > 100) return "负责人或报修人过长（最多 100 个字符）";
        if (order.PlannedStartTs is long start && order.PlannedEndTs is long end && end < start) return "计划结束时间不能早于计划开始时间";
        if (order.DowntimeMinutes is < 0 or > 100000) return "停机分钟数须为 0～100000";
        return null;
    }

    public static string? SparePart(SparePartEntity part)
    {
        if (string.IsNullOrWhiteSpace(part.Code) || part.Code.Trim().Length > 64) return "备件编号须为 1～64 个字符";
        if (string.IsNullOrWhiteSpace(part.Name) || part.Name.Trim().Length > 100) return "备件名称须为 1～100 个字符";
        if (part.Specification.Length > 128 || part.Unit.Length > 32 || part.Location.Length > 128 || part.Supplier.Length > 128)
            return "规格、单位、库位或供应商过长";
        if (!double.IsFinite(part.Stock) || part.Stock < 0) return "库存数量须为非负有限数值";
        if (!double.IsFinite(part.MinStock) || part.MinStock < 0) return "最低库存须为非负有限数值";
        if (part.UnitPrice is double price && (!double.IsFinite(price) || price < 0)) return "单价须为非负有限数值";
        if (part.Description.Length > 500) return "备件描述过长（最多 500 个字符）";
        return null;
    }

    public static string? Movement(SparePartMovementType type, double quantity, string note)
    {
        if (!Enum.IsDefined(type)) return "出入库类型无效";
        if (!double.IsFinite(quantity) || (type != SparePartMovementType.Adjust && quantity <= 0) || (type == SparePartMovementType.Adjust && quantity < 0))
            return type == SparePartMovementType.Adjust ? "盘点结存须为非负有限数值" : "出入库数量须大于 0";
        if (note.Length > 500) return "备注过长（最多 500 个字符）";
        return null;
    }

    public static string NewWorkOrderNumber(long ts)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(ts).ToLocalTime();
        return $"WO-{local:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
    }
}
