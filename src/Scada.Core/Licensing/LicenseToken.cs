using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scada.Core.Licensing;

/// <summary>
/// 激活码格式：<c>SCADA1.&lt;base64url(payload)&gt;.&lt;base64url(ECDSA-P256 签名)&gt;</c>。
/// 签名对象是 base64url 载荷的 ASCII 字节，因此重新编码载荷就会验签失败。
/// 客户端只持有公钥，无法伪造；私钥仅存在于厂商离线签发环境。
/// </summary>
public static class LicenseToken
{
    public const string Prefix = "SCADA1";
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        // 中文客户名保持可读；激活码本身是 Base64，不影响体积。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Create(LicensePayload payload, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload with { Version = CurrentVersion }, Json);
        var segment = Base64Url.Encode(bytes);
        var signature = signingKey.SignData(Encoding.ASCII.GetBytes(segment), HashAlgorithmName.SHA256);
        return $"{Prefix}.{segment}.{Base64Url.Encode(signature)}";
    }

    public static LicenseParseResult Parse(string? code, ECDsa verificationKey)
    {
        ArgumentNullException.ThrowIfNull(verificationKey);
        if (string.IsNullOrWhiteSpace(code))
        {
            return LicenseParseResult.Invalid("激活码为空");
        }

        var parts = code.Trim().Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            return LicenseParseResult.Invalid("激活码格式不正确");
        }

        byte[] signature;
        byte[] payloadBytes;
        try
        {
            payloadBytes = Base64Url.Decode(parts[1]);
            signature = Base64Url.Decode(parts[2]);
        }
        catch (FormatException)
        {
            return LicenseParseResult.Invalid("激活码编码损坏");
        }

        try
        {
            if (!verificationKey.VerifyData(Encoding.ASCII.GetBytes(parts[1]), signature, HashAlgorithmName.SHA256))
            {
                return LicenseParseResult.Invalid("激活码签名无效");
            }
        }
        catch (CryptographicException)
        {
            return LicenseParseResult.Invalid("激活码签名无效");
        }

        LicensePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicensePayload>(payloadBytes, Json);
        }
        catch (JsonException)
        {
            return LicenseParseResult.Invalid("激活码内容损坏");
        }

        if (payload is null || payload.Version != CurrentVersion || string.IsNullOrWhiteSpace(payload.LicenseId))
        {
            return LicenseParseResult.Invalid("激活码版本或编号无效");
        }
        if (payload.ExpiresAt <= 0)
        {
            return LicenseParseResult.Invalid("激活码缺少有效期");
        }

        return new LicenseParseResult(true, payload, null);
    }
}

public sealed record LicenseParseResult(bool Valid, LicensePayload? Payload, string? Error)
{
    public static LicenseParseResult Invalid(string error) => new(false, null, error);
}