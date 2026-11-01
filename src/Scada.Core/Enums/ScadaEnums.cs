namespace Scada.Core.Enums;

public enum TagKind { Physical = 0, Calculated = 1, Derived = 2 }

public enum ProtocolType
{
    ModbusTcp = 0,
    ModbusRtu = 1,
    SiemensS7 = 2,
    OpcUa = 3,
    Mqtt = 4,
    SparkplugB = 5,
    MitsubishiMc = 6,
    OmronFins = 7,
    Iec104 = 8,
    BacnetIp = 9,
    OpcDa = 10,
    Iec61850 = 11,
    ModbusAscii = 12,
    ModbusRtuOverTcp = 13
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
    OpcNode = 9,
    // 文本地址：新协议统一使用，地址文本保存在 Tag.NodeId，语法由各协议的 IProtocolSpec 校验。
    Address = 10
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
    // 兼容旧数据的位翻转报警；新建位报警统一使用 Discrete + ActiveWhen.Change。
    StateChange = 4,
    // 位报警：按测点的 0/1 状态报警，触发条件由 ActiveWhen 指定。
    Discrete = 5
}

/// <summary>位报警触发条件：On = 读到 1 时报警，Off = 读到 0 时报警，Change = 每次 0/1 翻转报警。</summary>
public enum AlarmActiveWhen
{
    On = 0,
    Off = 1,
    Change = 2
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

/// <summary>报警类别：用于分组、筛选和治理，不直接决定优先级。</summary>
public enum AlarmCategory
{
    Process = 0,
    Equipment = 1,
    Safety = 2,
    Diagnostic = 3
}

/// <summary>报警后果：与允许响应时间共同决定自动优先级（ISA-18.2 简化矩阵）。</summary>
public enum AlarmConsequence
{
    Minor = 0,
    Major = 1,
    Severe = 2,
    Safety = 3
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
