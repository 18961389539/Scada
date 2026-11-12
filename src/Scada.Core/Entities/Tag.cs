using System.Text.Json.Serialization;
using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class Tag : IAuditedEntity
{
    public int Id { get; set; }
    public int? DeviceId { get; set; }
    // 仅计算测点使用：显式所属区域；为空时按全部输入测点的区域判定权限。
    public int? AreaId { get; set; }
    public TagKind Kind { get; set; }
    public string Expression { get; set; } = "";
    public bool CalculationEnabled { get; set; } = true;
    public int CalculationIntervalMs { get; set; } = 1000;
    public int InputMaxAgeMs { get; set; } = 15000;
    public long CalculationRevision { get; set; } = 1;
    // 仅派生测点（TagKind.Derived）使用：所属计算任务与输出名，由计算任务维护，不能单独编辑。
    public int? JobId { get; set; }
    public string? JobOutput { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TagArea Area { get; set; } = TagArea.HoldingRegister;
    // 采集周期（毫秒）：为空表示跟随设备 PollIntervalMs。仅物理测点有效，
    // 同一设备按不同周期分成独立采集档位，避免少量快点把整台设备拉到同一节奏。
    public int? ScanRateMs { get; set; }
    public int Address { get; set; }
    public int DbNumber { get; set; } = 1;
    public int BitOffset { get; set; }
    public string NodeId { get; set; } = "";
    public TagDataType DataType { get; set; } = TagDataType.Float;
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string? Unit { get; set; }
    public bool Writable { get; set; }
    public ArchiveMode ArchiveMode { get; set; } = ArchiveMode.OnChange;
    public double Deadband { get; set; }
    public int ArchivePeriodSec { get; set; } = 600;
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }

    public Tag Clone() => (Tag)MemberwiseClone();

    // 审计拦截器据此生成「创建/修改/删除测点：名称」，写入口不必再各自拼一遍。
    // [JsonIgnore]：导出直接序列化实体，派生属性不该进工程包与接口契约。
    [JsonIgnore] public string AuditTargetType => "Tag";
    [JsonIgnore] public string AuditLabel => "测点";
    [JsonIgnore] public string AuditName => Name;

    // 编辑物理测点时把请求值合并回被跟踪的实体。Id/Kind/计算字段刻意不参与：
    // 它们由所属设备与计算任务维护，混进来会把一个测点改成另一个测点。
    // 新增可编辑字段时改这一处即可——端点与 MCP 之前各抄了一份 19 行的字段清单。
    public void ApplyEditableFrom(Tag source)
    {
        DeviceId = source.DeviceId;
        Name = source.Name;
        Description = source.Description;
        Area = source.Area;
        Address = source.Address;
        DbNumber = source.DbNumber;
        BitOffset = source.BitOffset;
        NodeId = source.NodeId;
        DataType = source.DataType;
        Scale = source.Scale;
        Offset = source.Offset;
        Unit = source.Unit;
        Writable = source.Writable;
        ArchiveMode = source.ArchiveMode;
        Deadband = source.Deadband;
        ScanRateMs = source.ScanRateMs;
        ArchivePeriodSec = source.ArchivePeriodSec;
        MinValue = source.MinValue;
        MaxValue = source.MaxValue;
    }
}
