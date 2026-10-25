using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scada.Server.External.Tcp;

// 长度前缀帧：4 字节大端长度 + UTF-8 JSON（不含 Framing 本身）。
public static class TcpFraming
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static byte[] Encode(object message)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        var frame = new byte[4 + body.Length];
        frame[0] = (byte)((body.Length >> 24) & 0xff);
        frame[1] = (byte)((body.Length >> 16) & 0xff);
        frame[2] = (byte)((body.Length >> 8) & 0xff);
        frame[3] = (byte)(body.Length & 0xff);
        Buffer.BlockCopy(body, 0, frame, 4, body.Length);
        return frame;
    }

    public static bool TryReadFrame(ref byte[] buffer, ref int length, int maxFrame, out byte[]? payload, out string? error)
    {
        payload = null; error = null;
        if (length < 4) return false;
        var size = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
        if (size <= 0 || size > maxFrame) { error = "frame_too_large"; return false; }
        if (length < 4 + size) return false;
        payload = new byte[size];
        Buffer.BlockCopy(buffer, 4, payload, 0, size);
        var remain = length - 4 - size;
        if (remain > 0) Buffer.BlockCopy(buffer, 4 + size, buffer, 0, remain);
        length = remain;
        return true;
    }
}

public sealed class TcpMessage
{
    public string Type { get; set; } = "";
    public string? Id { get; set; }
    public string? ApiKey { get; set; }
    public int[]? TagIds { get; set; }
    public int? TagId { get; set; }
    public double? Value { get; set; }
    public bool? Confirmed { get; set; }
    public Guid? RequestId { get; set; }
    public string? Message { get; set; }
    public string? Code { get; set; }
    public object? Data { get; set; }
}

public static class TcpErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string BadRequest = "bad_request";
    public const string RateLimited = "rate_limited";
    public const string NotFound = "not_found";
    public const string Internal = "internal";
    public const string FrameTooLarge = "frame_too_large";
}
