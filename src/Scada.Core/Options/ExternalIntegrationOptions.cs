using Scada.Core.Enums;

namespace Scada.Core.Options;

public sealed class ExternalIntegrationOptions
{
    public ExternalRestOptions Rest { get; set; } = new();
    public ExternalTcpOptions Tcp { get; set; } = new();
    public ExternalOpcUaOptions OpcUa { get; set; } = new();
    public ModbusSlaveOptions ModbusSlave { get; set; } = new();
    public Iec104ServerOptions Iec104Server { get; set; } = new();
}

public sealed class ExternalRestOptions
{
    public bool Enabled { get; set; } = true;
    // 额外 CORS 来源（与内部 web 策略分离）；空 = 不额外放行。
    public string[] CorsOrigins { get; set; } = [];
    public int DefaultRateLimitPerMinute { get; set; } = 120;
    // 是否允许用登录 JWT 访问 /api/v1（权限按用户区域授权映射到 scopes）。
    public bool AllowJwt { get; set; } = true;
}

public sealed class ExternalTcpOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 7500;
    public bool Tls { get; set; }
    public string? TlsCertPath { get; set; }
    public string? TlsCertPassword { get; set; }
    public int MaxConnections { get; set; } = 64;
    public string[] IpAllowlist { get; set; } = [];
    public int IdleTimeoutSec { get; set; } = 120;
    public int MaxFrameBytes { get; set; } = 256 * 1024;
}

public sealed class ExternalOpcUaOptions
{
    public bool Enabled { get; set; }
    public string EndpointUrl { get; set; } = "opc.tcp://0.0.0.0:4840/Scada";
    // None / Sign / SignAndEncrypt（可多选，逗号分隔）。
    public string SecurityModes { get; set; } = "None,Sign,SignAndEncrypt";
    public bool AllowAnonymous { get; set; }
    public bool AllowUsernamePassword { get; set; } = true;
    public bool AllowCertificateUser { get; set; }
    // 自动信任任意客户端证书。默认关闭：证书身份必须由运维在 PKI 目录里显式信任。
    public bool AutoAcceptUntrustedCertificates { get; set; }
    // 空 = 使用 keys/opcua-server.pfx，不存在则自动生成自签名。
    public string? ServerCertPath { get; set; }
    public string? ServerCertPassword { get; set; }
    public int MaxSessionCount { get; set; } = 50;
    public int PublishingIntervalMs { get; set; } = 500;
    // 是否把报警规则挂成 AlarmConditionType 实例供上级系统订阅（A&C）。
    public bool ExposeAlarmConditions { get; set; } = true;
    // 报警状态扫描周期，决定上级系统看到 Active/Acked 变化的延迟。
    public int AlarmPublishIntervalMs { get; set; } = 500;
}

// 对外 Modbus TCP 从站：让上级 SCADA/PLC/数采网关按标准 Modbus 读本系统的实时值，并可写回控制点。
// 默认关闭。AutoAddressing 打开时按测点自身的 Area+Address 直通，等价于把本系统当成协议转换网关。
public sealed class ModbusSlaveOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 502;
    // 允许访问的从站地址（Unit Id）。主站用其它 Unit Id 时按首个处理。
    public int[] UnitIds { get; set; } = [1];
    // "*"、单个地址或 CIDR。空 = 拒绝所有来源；放开必须显式配置。
    public string[] IpAllowlist { get; set; } = [];
    public int MaxConnections { get; set; } = 16;
    public int IdleTimeoutSec { get; set; } = 120;
    public bool AutoAddressing { get; set; } = true;
    // 从站侧没有字节序握手，按现场约定统一编码；与采集侧 ByteOrder 无关。
    public ByteOrder ByteOrder { get; set; } = ByteOrder.ABCD;
    // 质量不是 Good 时返回的替代值。null = 保持最后一次有效值（多数主站期望的行为）。
    public double? BadQualityValue { get; set; }
    // 默认只读：写回等于把外部网络接进控制闭环，必须在部署时显式打开。
    public bool AllowWrite { get; set; }
    // 单次读数量上限，同时用于拒绝异常请求；标准上限 125 寄存器 / 2000 位。
    public int MaxReadRegisters { get; set; } = 125;
    public int MaxReadBits { get; set; } = 2000;
    public List<ModbusSlavePoint> Points { get; set; } = [];
}

public sealed class ModbusSlavePoint
{
    public int TagId { get; set; }
    public int UnitId { get; set; } = 1;
    public TagArea Area { get; set; } = TagArea.HoldingRegister;
    public int Address { get; set; }
}

// 对外 IEC 60870-5-104 从站（被控站）：向调度主站上送遥测/遥信，接受总召唤、对时与遥控。
// 104 没有"地址直通"概念（IOA 与现场地址无对应关系），必须显式配置点表。
public sealed class Iec104ServerOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 2404;
    public int[] CommonAddresses { get; set; } = [1];
    // "*"、单个地址或 CIDR。空 = 拒绝所有来源；放开必须显式配置。
    public string[] IpAllowlist { get; set; } = [];
    public int MaxConnections { get; set; } = 4;
    public int IdleTimeoutSec { get; set; } = 300;
    // k：未被确认的 I 帧上限；w：收到多少个 I 帧后必须发 S 帧确认。
    public int K { get; set; } = 12;
    public int W { get; set; } = 8;
    public int T1Sec { get; set; } = 15;
    public int T2Sec { get; set; } = 10;
    public int T3Sec { get; set; } = 20;
    // 变化上送是否带 CP56Time2a 时标（M_SP_TB_1 / M_ME_TF_1）。
    public bool UseTimeTag { get; set; } = true;
    // 默认只读：遥控等于把调度网络接进控制闭环，必须在部署时显式打开。
    public bool AllowControl { get; set; }
    // 变化扫描周期：越小越实时，但会增加 CPU 与报文数量。
    public int ChangeScanMs { get; set; } = 200;
    public List<Iec104ServerPoint> Points { get; set; } = [];
}

public sealed class Iec104ServerPoint
{
    public int TagId { get; set; }
    public int Ioa { get; set; }
    public Iec104PointKind Kind { get; set; } = Iec104PointKind.Auto;
}

public enum Iec104PointKind
{
    // 按测点数据类型选择：Bool → 单点遥信，其余 → 短浮点遥测。
    Auto = 0,
    SinglePoint = 1,
    MeasuredFloat = 2
}
