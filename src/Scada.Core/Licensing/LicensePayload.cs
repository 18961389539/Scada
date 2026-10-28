using System.Text.Json.Serialization;

namespace Scada.Core.Licensing;

/// <summary>
/// 激活码载荷。字段名保持短小固定：激活码会被现场人员手工复制，格式一旦发布就不能再改。
/// </summary>
public sealed record LicensePayload
{
    [JsonPropertyName("v")] public int Version { get; init; } = 1;
    [JsonPropertyName("kid")] public string KeyId { get; init; } = "";
    [JsonPropertyName("lic")] public string LicenseId { get; init; } = "";
    [JsonPropertyName("cus")] public string Customer { get; init; } = "";
    /// <summary>绑定的机器指纹；为空表示不绑定（不推荐，仅用于演示授权）。</summary>
    [JsonPropertyName("mid")] public string MachineId { get; init; } = "";
    /// <summary>签发时间（Unix 秒）。</summary>
    [JsonPropertyName("iat")] public long IssuedAt { get; init; }
    /// <summary>到期时间（Unix 秒）。</summary>
    [JsonPropertyName("exp")] public long ExpiresAt { get; init; }
    /// <summary>授权上限；0 表示不限。</summary>
    [JsonPropertyName("dev")] public int MaxDevices { get; init; }
    [JsonPropertyName("tag")] public int MaxTags { get; init; }
    /// <summary>功能位：ha / opcua / report-signing / csharp-scripts / traceability / oee。</summary>
    [JsonPropertyName("feat")] public string[] Features { get; init; } = [];
}

public static class LicenseFeatures
{
    public const string Ha = "ha";
    public const string OpcUa = "opcua";
    public const string ReportSigning = "report-signing";
    public const string CSharpScripts = "csharp-scripts";
    public const string Traceability = "traceability";
    public const string Oee = "oee";
}