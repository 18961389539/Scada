using System.Text;

namespace Scada.Core.Licensing;

/// <summary>URL/文件名安全的 Base64（无填充），激活码可被安全地复制粘贴。</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        var padding = normalized.Length % 4;
        if (padding is 2 or 3)
        {
            normalized += new string('=', 4 - padding);
        }
        return Convert.FromBase64String(normalized);
    }

    public static string EncodeUtf8(string value) => Encode(Encoding.UTF8.GetBytes(value));
}