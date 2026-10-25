namespace Scada.Core.Entities;

[Flags]
public enum ExternalApiScope
{
    None = 0,
    ReadTags = 1,
    ReadHistory = 2,
    ReadAlarms = 4,
    Write = 8,
    All = ReadTags | ReadHistory | ReadAlarms | Write
}

// 对外接口 API 密钥：明文只在创建时返回一次，库中只存 SHA256。
public class ExternalApiKey
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    // 展示用前缀（如 scada_ab12），便于识别，不含秘密。
    public string KeyPrefix { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public ExternalApiScope Scopes { get; set; } = ExternalApiScope.ReadTags;
    // JSON 数组；空/null = 全部区域（仍受 Scopes 限制）。
    public string? AreaIdsJson { get; set; }
    public int RateLimitPerMinute { get; set; } = 120;
    public bool Enabled { get; set; } = true;
    public long CreatedTs { get; set; }
    public string? CreatedBy { get; set; }
    public long? LastUsedTs { get; set; }
    public long? RevokedTs { get; set; }
    public string? Notes { get; set; }
}
