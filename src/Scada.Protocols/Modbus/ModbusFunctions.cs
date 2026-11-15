using Scada.Core.Enums;

namespace Scada.Protocols.Modbus;

/// <summary>Modbus 功能码。区域到读功能码的映射只有这一份，采集与从站都从这里取。</summary>
public static class ModbusFunctions
{
    public const byte ReadCoils = 1;
    public const byte ReadDiscreteInputs = 2;
    public const byte ReadHoldingRegisters = 3;
    public const byte ReadInputRegisters = 4;

    /// <summary>读功能码：线圈 1、离散输入 2、保持寄存器 3、输入寄存器 4。</summary>
    public static byte ForArea(TagArea area) => area switch
    {
        TagArea.Coil => ReadCoils,
        TagArea.DiscreteInput => ReadDiscreteInputs,
        TagArea.HoldingRegister => ReadHoldingRegisters,
        TagArea.InputRegister => ReadInputRegisters,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "该区域不支持 Modbus 读功能码")
    };

    /// <summary>该区域是否是位（线圈/离散输入）区域。</summary>
    public static bool IsBitArea(TagArea area) => area is TagArea.Coil or TagArea.DiscreteInput;
}
