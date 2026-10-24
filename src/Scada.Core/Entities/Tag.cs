using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class Tag
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
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public TagArea Area { get; set; } = TagArea.HoldingRegister;
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

    public int ByteCount => DataType == TagDataType.Bool ? 1 : RegisterCount * 2;

    public int RegisterCount => DataType switch
    {
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 2,
        TagDataType.Double => 4,
        _ => 1
    };
}
