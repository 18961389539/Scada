using Scada.Core.Enums;

namespace Scada.Core.Entities;

public class Device
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

    public Device Clone() => (Device)MemberwiseClone();
}
