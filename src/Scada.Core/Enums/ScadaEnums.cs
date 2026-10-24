namespace Scada.Core.Enums;

public enum TagKind { Physical = 0, Calculated = 1, Derived = 2 }

public enum ProtocolType
{
    ModbusTcp = 0,
    ModbusRtu = 1,
    SiemensS7 = 2,
    OpcUa = 3
}
public enum OpcSecurityMode { None = 0, Sign = 1, SignAndEncrypt = 2 }
public enum OpcSecurityPolicy { Basic256Sha256 = 0, Aes128Sha256RsaOaep = 1, Aes256Sha256RsaPss = 2, None = 3 }

public enum SerialParity { None = 0, Odd = 1, Even = 2 }
public enum SerialStopBits { One = 1, Two = 2 }
public enum S7Cpu { S7200 = 0, S7300 = 1, S7400 = 2, S71200 = 3, S71500 = 4 }

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
    InputRegister = 4,
    DataBlock = 5,
    Memory = 6,
    Input = 7,
    Output = 8,
    OpcNode = 9
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

public enum AlarmSuppressMode
{
    None = 0,
    WhenOn = 1,   // 条件测点为 1（>=0.5）时抑制
    WhenOff = 2   // 条件测点为 0 时抑制，例如设备运行信号=0（停机）
}

public enum NotificationChannelType
{
    Email = 0,
    Webhook = 1,
    SmsHttp = 2
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
    Admin = 2,
    // 无全局内置角色：只通过区域授权获得权限。
    None = 3
}

public enum TagQuality
{
    Good = 0,
    Bad = 1,
    CommFailure = 2,
    Uncertain = 3
}
