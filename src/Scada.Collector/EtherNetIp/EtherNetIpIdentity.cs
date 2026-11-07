using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Scada.Collector.EtherNetIp;

/// <summary>EtherNet/IP ListIdentity（封装命令 0x0063）返回的设备身份信息。</summary>
public sealed record EtherNetIpIdentity(
    ushort VendorId,
    ushort DeviceType,
    ushort ProductCode,
    byte RevisionMajor,
    byte RevisionMinor,
    ushort Status,
    uint SerialNumber,
    string ProductName);

/// <summary>
/// 连接探针：向设备发送 ListIdentity 请求，确认端口上确实是 EtherNet/IP 端点。
/// 只做一次短连接，实际采集由 libplctag 自行管理标签连接。
/// </summary>
public static class EtherNetIpIdentityReader
{
    private const ushort ListIdentity = 0x0063;
    private const ushort IdentityItem = 0x0001;
    private const int EncapsulationHeader = 24;

    public static async Task<EtherNetIpIdentity?> ReadAsync(string host, int port, int timeoutMs, CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Math.Clamp(timeoutMs, 500, 30000));
        await client.ConnectAsync(host, port, deadline.Token);
        client.NoDelay = true;
        using var stream = client.GetStream();

        var request = new byte[EncapsulationHeader];
        BinaryPrimitives.WriteUInt16LittleEndian(request, ListIdentity);
        await stream.WriteAsync(request, deadline.Token);

        var header = new byte[EncapsulationHeader];
        await ReadExactAsync(stream, header, deadline.Token);
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != ListIdentity)
            throw new IOException("设备返回的封装命令不是 ListIdentity");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        if (length is 0 or > 0x4000) throw new IOException($"ListIdentity 响应长度异常（{length}）");

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, deadline.Token);
        return Parse(payload);
    }

    /// <summary>解析 ListIdentity 响应载荷；格式不完整或不含 Identity 项时返回 null。</summary>
    public static EtherNetIpIdentity? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        var offset = 2;
        for (var i = 0; i < count; i++)
        {
            if (offset + 4 > payload.Length) return null;
            var type = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(payload[(offset + 2)..]);
            offset += 4;
            if (length > payload.Length - offset) return null;
            if (type == IdentityItem) return ParseIdentity(payload.Slice(offset, length));
            offset += length;
        }
        return null;
    }

    private static EtherNetIpIdentity? ParseIdentity(ReadOnlySpan<byte> item)
    {
        // 协议版本 2 + 套接字地址 16 + 厂商/设备类型/产品代码 6 + 版本 2 + 状态 2 + 序列号 4 + 名称长度 1 + 状态 1
        if (item.Length < 34) return null;
        var pos = 2; // 协议版本
        pos += 16;   // 套接字地址不参与身份展示
        var vendor = BinaryPrimitives.ReadUInt16LittleEndian(item[pos..]); pos += 2;
        var deviceType = BinaryPrimitives.ReadUInt16LittleEndian(item[pos..]); pos += 2;
        var productCode = BinaryPrimitives.ReadUInt16LittleEndian(item[pos..]); pos += 2;
        var major = item[pos];
        var minor = item[pos + 1];
        pos += 2;
        var status = BinaryPrimitives.ReadUInt16LittleEndian(item[pos..]); pos += 2;
        var serial = BinaryPrimitives.ReadUInt32LittleEndian(item[pos..]); pos += 4;
        var nameLength = item[pos];
        pos += 1;
        if (nameLength > item.Length - pos - 1) return null;
        var name = Encoding.ASCII.GetString(item.Slice(pos, nameLength)).TrimEnd('\0', ' ');
        return new EtherNetIpIdentity(vendor, deviceType, productCode, major, minor, status, serial, name);
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0) throw new IOException("连接在读取 ListIdentity 响应前关闭");
            read += count;
        }
    }
}
