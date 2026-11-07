namespace Scada.Core.Entities;

public enum SpcChartType { Individuals = 0, XbarR = 1 }

public enum SpcViolationRule { Beyond3Sigma = 0, TwoOfThreeBeyond2Sigma = 1, FourOfFiveBeyond1Sigma = 2, EightOnOneSide = 3, RangeBeyondLimit = 4 }

/// <summary>SPC 控制图配置：每个图绑定一个测点，按采样周期与子组大小形成子组。</summary>
public sealed class SpcChart
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int AreaId { get; set; } = Area.DefaultId;
    public int TagId { get; set; }
    public SpcChartType ChartType { get; set; } = SpcChartType.Individuals;
    public int SubgroupSize { get; set; } = 1;
    public int SamplingIntervalSec { get; set; } = 60;
    public int BaselineSubgroups { get; set; } = 25;
    public bool AutoLimits { get; set; } = true;
    public double? CenterLine { get; set; }
    public double? UpperControlLimit { get; set; }
    public double? LowerControlLimit { get; set; }
    public double? RangeCenter { get; set; }
    public double? RangeUpperControlLimit { get; set; }
    public double? RangeLowerControlLimit { get; set; }
    // 自动控制限建立时刻；为空表示仍在用临时控制限，达到基线子组数后冻结。
    public long? LimitsEstablishedTs { get; set; }
    public double? SpecUpperLimit { get; set; }
    public double? SpecLowerLimit { get; set; }
    public bool RuleBeyond3Sigma { get; set; } = true;
    public bool RuleTwoOfThreeBeyond2Sigma { get; set; } = true;
    public bool RuleFourOfFiveBeyond1Sigma { get; set; } = true;
    public bool RuleEightOnOneSide { get; set; } = true;
    public bool RuleRangeBeyondLimit { get; set; } = true;
    public string Description { get; set; } = "";
    // 采样代次：采样、违规记录都带代次。测点/图类型/子组/周期/基线/控制限变化时递增，
    // 旧样本不参与新图计算；仅改名称、说明或启用状态时保持不变。
    public int Revision { get; set; } = 1;
    // 并发版本号：任何修改都递增，用于乐观并发校验。
    public int ConfigVersion { get; set; } = 1;
    public long CreatedTs { get; set; }
    public long UpdatedTs { get; set; }
    public string UpdatedBy { get; set; } = "";
}

/// <summary>SPC 子组样本：均值图与极差图共用一个记录，控制限快照按采样时刻保存。</summary>
public sealed class SpcSample
{
    public long Id { get; set; }
    public int ChartId { get; set; }
    public int Revision { get; set; }
    public long Ts { get; set; }
    public double Mean { get; set; }
    public double Range { get; set; }
    public int SubgroupSize { get; set; }
    public string ValuesJson { get; set; } = "[]";
    public double? CenterLine { get; set; }
    public double? UpperControlLimit { get; set; }
    public double? LowerControlLimit { get; set; }
    public double? RangeCenter { get; set; }
    public double? RangeUpperControlLimit { get; set; }
    public double? RangeLowerControlLimit { get; set; }
    // 逗号分隔的规则编码，例如 Beyond3Sigma,RangeBeyondLimit。
    public string RuleCodes { get; set; } = "";
    public double? Cp { get; set; }
    public double? Cpk { get; set; }
}

/// <summary>SPC 判异事件：同一图同一规则持续失控时只保留一条未恢复记录。</summary>
public sealed class SpcViolation
{
    public long Id { get; set; }
    public int ChartId { get; set; }
    public int Revision { get; set; }
    public long SampleId { get; set; }
    public SpcViolationRule Rule { get; set; }
    public long Ts { get; set; }
    public int? AlarmEventId { get; set; }
    public long? RecoveredTs { get; set; }
    public string Message { get; set; } = "";
}
