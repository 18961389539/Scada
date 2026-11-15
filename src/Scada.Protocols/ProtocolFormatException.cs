namespace Scada.Protocols;

/// <summary>
/// 收到的报文不符合协议规范（长度不符、地址越界、元素截断等）。
/// 继承 <see cref="IOException"/>：链路层原有的"帧坏了就断链重连"分支不需要逐处修改，
/// 而需要区分"帧坏了"与"设备在线但拒绝了请求"的地方可以精确捕获本类型。
/// </summary>
public sealed class ProtocolFormatException : IOException
{
    public ProtocolFormatException(string protocol, string detail, int? offset = null, Exception? inner = null)
        : base(Describe(protocol, detail, offset), inner)
    {
        Protocol = protocol;
        Offset = offset;
    }

    /// <summary>协议名，如 "Modbus"、"IEC104"。</summary>
    public string Protocol { get; }

    /// <summary>出错字节在报文内的偏移；无法定位时为 null。</summary>
    public int? Offset { get; }

    private static string Describe(string protocol, string detail, int? offset) =>
        offset is int position ? $"{protocol} 报文不合规（偏移 {position}）：{detail}" : $"{protocol} 报文不合规：{detail}";
}
