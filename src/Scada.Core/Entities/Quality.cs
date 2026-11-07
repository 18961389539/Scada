namespace Scada.Core.Entities;

public enum QualityResult { Passed = 0, Failed = 1, Rework = 2, Scrap = 3 }

/// <summary>缺陷代码字典：质量分析的 Pareto 与根因统计按该字典分类。</summary>
public sealed class QualityDefectCode
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Description { get; set; } = "";
    public long CreatedTs { get; set; }
    public long UpdatedTs { get; set; }
}

/// <summary>质量检验记录：一条记录代表一个被检验对象，可按 SN/批次、产品、设备与缺陷代码统计。</summary>
public sealed class QualityInspection
{
    public int Id { get; set; }
    public string Sn { get; set; } = "";
    public string BatchNo { get; set; } = "";
    public string Product { get; set; } = "";
    public int AreaId { get; set; } = Area.DefaultId;
    public int? DeviceId { get; set; }
    // 可选的过程测点：用于从缺陷记录直接下钻到检验时刻附近的趋势曲线。
    public int? TagId { get; set; }
    public QualityResult Result { get; set; } = QualityResult.Passed;
    public int? DefectCodeId { get; set; }
    public string Inspector { get; set; } = "";
    public long Ts { get; set; }
    public string Note { get; set; } = "";
    public long CreatedTs { get; set; }
    public long UpdatedTs { get; set; }
    public string CreatedBy { get; set; } = "";
}
