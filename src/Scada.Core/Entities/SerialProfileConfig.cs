using System.Text.Json.Serialization;
using Scada.Core.Enums;
using Scada.Core.Scripting;

namespace Scada.Core.Entities;

// 串口配置入库后由 Worker 宿主持有；名称是脚本 context.Serial 的引用键。
public class SerialProfileConfig : IAuditedEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Port { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public SerialParity Parity { get; set; }
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;
    public string Encoding { get; set; } = "utf-8";
    public string NewLine { get; set; } = "\r\n";
    public int ReadTimeoutMs { get; set; } = 2000;
    public int MaxReplyChars { get; set; } = SerialProfileRules.MaxReplyChars;
    public bool DtrEnable { get; set; }
    public bool RtsEnable { get; set; }
    public long Revision { get; set; } = 1;

    public SerialProfile CloneProfile() => new()
    {
        Port = Port, BaudRate = BaudRate, DataBits = DataBits, Parity = Parity, StopBits = StopBits,
        Encoding = Encoding, NewLine = NewLine, ReadTimeoutMs = ReadTimeoutMs, MaxReplyChars = MaxReplyChars,
        DtrEnable = DtrEnable, RtsEnable = RtsEnable
    };

    [JsonIgnore] public string AuditTargetType => "SerialProfile";
    [JsonIgnore] public string AuditLabel => "串口配置";
    [JsonIgnore] public string AuditName => Name;
}
