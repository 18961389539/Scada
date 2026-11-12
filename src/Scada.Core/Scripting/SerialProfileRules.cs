namespace Scada.Core.Scripting;

// 串口配置的公共校验：Worker 宿主、配置目录和 API 必须用同一套边界。
public static class SerialProfileRules
{
    public const int MaxNameLength = 64;
    public const int MaxPortLength = 128;
    public const int MaxCommandChars = 512;
    public const int MaxReplyChars = 4096;
    public const int MinReadTimeoutMs = 50;
    public const int MaxReadTimeoutMs = 60_000;

    public static string? Validate(string? name, SerialProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength) return "串口配置名称须为 1～64 个字符";
        if (profile is null) return "串口参数缺失";
        if (string.IsNullOrWhiteSpace(profile.Port) || profile.Port.Trim().Length > MaxPortLength) return $"串口配置「{name.Trim()}」须填写 1～128 个字符的端口";
        if (profile.BaudRate is < 300 or > 921_600 || profile.DataBits is < 5 or > 8) return $"串口配置「{name.Trim()}」的波特率或数据位无效";
        if (profile.ReadTimeoutMs is < MinReadTimeoutMs or > MaxReadTimeoutMs || profile.MaxReplyChars is < 1 or > MaxReplyChars)
            return $"串口配置「{name.Trim()}」的读取超时或回复长度上限无效";
        if (!ValidEncoding(profile.Encoding)) return $"串口配置「{name.Trim()}」只支持 utf-8 或 ascii 编码";
        if (!ValidNewLine(profile.NewLine)) return $"串口配置「{name.Trim()}」只支持 CRLF、LF 或 CR 换行";
        return null;
    }

    public static bool ValidEncoding(string? value) => (value ?? "").Trim().ToLowerInvariant() is "utf-8" or "utf8" or "ascii" or "us-ascii";

    public static bool ValidNewLine(string? value) => value is "\r\n" or "\n" or "\r" or "";

    public static bool SamePort(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left.Trim(), right.Trim(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
