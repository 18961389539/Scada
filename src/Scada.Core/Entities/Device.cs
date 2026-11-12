using Scada.Core.Enums;
using System.Text.Json.Serialization;

namespace Scada.Core.Entities;

public class Device : IAuditedEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public ProtocolType Protocol { get; set; } = ProtocolType.ModbusTcp;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;
    public byte StationNumber { get; set; } = 1;
    public ByteOrder ByteOrder { get; set; } = ByteOrder.ABCD;
    public int PollIntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 3000;
    public bool Enabled { get; set; } = true;
    public int? GatewayId { get; set; }
    // 绑定的物理通道。设置后端点（Host/Port 或串口参数）由通道下发，多台设备共用一条连接。
    public int? ChannelId { get; set; }
    public int AreaId { get; set; } = Area.DefaultId;
    public long DisabledSinceTs { get; set; }
    public string SerialPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; }
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;
    public S7Cpu Cpu { get; set; } = S7Cpu.S71200;
    public short Rack { get; set; }
    public short Slot { get; set; } = 1;
    public string OpcEndpointUrl { get; set; } = "opc.tcp://localhost:4840";
    public OpcSecurityMode OpcSecurityMode { get; set; } = OpcSecurityMode.SignAndEncrypt;
    public OpcSecurityPolicy OpcSecurityPolicy { get; set; } = OpcSecurityPolicy.Basic256Sha256;
    public string OpcUserName { get; set; } = "";
    [JsonIgnore] public string? OpcPasswordProtected { get; set; }
    // 协议专属参数（JSON 对象），由 ProtocolCatalog 中对应协议校验；用户名/密码复用 OpcUserName/OpcPasswordProtected。
    public string ProtocolOptions { get; set; } = "";
    // 仿真设备不连接现场，读写都落在仿真运行时；参数为 SimulationDeviceOptions JSON。
    public bool SimulationEnabled { get; set; }
    public string SimulationOptions { get; set; } = "";
    public bool HasOpcPassword => !string.IsNullOrEmpty(OpcPasswordProtected);

    public Device Clone() => (Device)MemberwiseClone();

    // 审计拦截器据此生成「修改设备：名称」这类记录，不必在六个写入口各写一遍。
    // [JsonIgnore]：导出直接序列化实体，派生属性不该进工程包与接口契约。
    [JsonIgnore] public string AuditTargetType => "Device";
    [JsonIgnore] public string AuditLabel => "设备";
    [JsonIgnore] public string AuditName => Name;

    // 编辑设备时把请求值合并回被跟踪的实体。Id/GatewayId/DisabledSinceTs 刻意不参与：
    // 前两个由归属与网关侧维护，DisabledSinceTs 由调用方按「本次是否由启用改为停用」决定。
    public void ApplyEditableFrom(Device source)
    {
        Name = source.Name;
        Protocol = source.Protocol;
        Host = source.Host;
        Port = source.Port;
        StationNumber = source.StationNumber;
        ByteOrder = source.ByteOrder;
        PollIntervalMs = source.PollIntervalMs;
        TimeoutMs = source.TimeoutMs;
        Enabled = source.Enabled;
        ChannelId = source.ChannelId;
        AreaId = source.AreaId;
        SerialPort = source.SerialPort;
        BaudRate = source.BaudRate;
        DataBits = source.DataBits;
        Parity = source.Parity;
        StopBits = source.StopBits;
        Cpu = source.Cpu;
        Rack = source.Rack;
        Slot = source.Slot;
        OpcEndpointUrl = source.OpcEndpointUrl;
        OpcSecurityMode = source.OpcSecurityMode;
        OpcSecurityPolicy = source.OpcSecurityPolicy;
        OpcUserName = source.OpcUserName;
        OpcPasswordProtected = source.OpcPasswordProtected;
        ProtocolOptions = source.ProtocolOptions;
        SimulationEnabled = source.SimulationEnabled;
        SimulationOptions = source.SimulationOptions;
    }
}
