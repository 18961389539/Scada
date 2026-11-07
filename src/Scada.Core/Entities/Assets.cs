using Scada.Core.Enums;

namespace Scada.Core.Entities;

public enum AssetStatus { InService = 0, Standby = 1, Maintenance = 2, Retired = 3 }
public enum WorkOrderType { Corrective = 0, Preventive = 1, Inspection = 2, Improvement = 3 }
public enum WorkOrderPriority { Low = 0, Normal = 1, High = 2, Urgent = 3 }
public enum WorkOrderStatus { Open = 0, InProgress = 1, OnHold = 2, Completed = 3, Cancelled = 4 }
public enum SparePartMovementType { In = 0, Out = 1, Adjust = 2 }

/// <summary>设备资产台账：可关联采集设备，也可独立登记电机、泵、阀等现场资产。</summary>
public sealed class Asset
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Model { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Location { get; set; } = "";
    public int AreaId { get; set; } = Area.DefaultId;
    public int? DeviceId { get; set; }
    public AssetStatus Status { get; set; } = AssetStatus.InService;
    public long? CommissionedTs { get; set; }
    public long? WarrantyUntilTs { get; set; }
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long CreatedTs { get; set; }
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>维护工单：故障、预防性维护、巡检与改进；状态流转和备件领用单独记录。</summary>
public sealed class MaintenanceWorkOrder
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Title { get; set; } = "";
    public int? AssetId { get; set; }
    public int? DeviceId { get; set; }
    public int AreaId { get; set; } = Area.DefaultId;
    public WorkOrderType Type { get; set; }
    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Normal;
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Open;
    public string Description { get; set; } = "";
    public string Assignee { get; set; } = "";
    public string Reporter { get; set; } = "";
    public long? PlannedStartTs { get; set; }
    public long? PlannedEndTs { get; set; }
    public long? StartedTs { get; set; }
    public long? CompletedTs { get; set; }
    public int DowntimeMinutes { get; set; }
    public string Resolution { get; set; } = "";
    public long CreatedTs { get; set; }
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>备件库存：库存数量使用小数，兼容按米、公斤和个数计量的备件。</summary>
public sealed class SparePart
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Specification { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Stock { get; set; }
    public double MinStock { get; set; }
    public string Location { get; set; } = "";
    public string Supplier { get; set; } = "";
    public double? UnitPrice { get; set; }
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long CreatedTs { get; set; }
    public long UpdatedTs { get; set; }
}

/// <summary>备件出入库流水；Adjust 表示盘点后直接设定结存。</summary>
public sealed class SparePartMovement
{
    public int Id { get; set; }
    public int PartId { get; set; }
    public SparePartMovementType Type { get; set; }
    public double Quantity { get; set; }
    public double BalanceAfter { get; set; }
    public int? WorkOrderId { get; set; }
    public string Operator { get; set; } = "";
    public string Note { get; set; } = "";
    public long Ts { get; set; }
}
