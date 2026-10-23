namespace Scada.Core.Enums;

public enum ProtocolType
{
    ModbusTcp = 0
}

public enum ByteOrder
{
    ABCD = 0,
    BADC = 1,
    CDAB = 2,
    DCBA = 3
}

public enum TagArea
{
    Coil = 1,
    DiscreteInput = 2,
    HoldingRegister = 3,
    InputRegister = 4
}

public enum TagDataType
{
    Bool = 0,
    Int16 = 1,
    UInt16 = 2,
    Int32 = 3,
    UInt32 = 4,
    Float = 5,
    Double = 6
}

public enum ArchiveMode
{
    None = 0,
    OnChange = 1,
    Periodic = 2
}

public enum AlarmRuleType
{
    High = 0,
    HighHigh = 1,
    Low = 2,
    LowLow = 3,
    StateChange = 4
}

public enum AlarmLevel
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public enum UserRole
{
    Viewer = 0,
    Operator = 1,
    Admin = 2
}

public enum TagQuality
{
    Good = 0,
    Bad = 1,
    CommFailure = 2
}
