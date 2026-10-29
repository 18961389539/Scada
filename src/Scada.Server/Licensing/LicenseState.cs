namespace Scada.Server.Licensing;

public enum LicenseStatus
{
    /// <summary>试用期内。</summary>
    Trial,
    /// <summary>已激活且有效。</summary>
    Active,
    /// <summary>已激活，但临近到期（前端需弹窗提醒）。</summary>
    ExpiringSoon,
    /// <summary>试用或授权已到期。</summary>
    Expired,
    /// <summary>激活码无效（签名、机器码、格式）。</summary>
    Invalid,
    /// <summary>检测到系统时间被回拨。</summary>
    Tampered
}

/// <summary>对外暴露的授权快照，同时用于 <c>/api/license</c> 响应。</summary>
public sealed record LicenseSnapshot(
    LicenseStatus Status,
    string MachineId,
    string Message,
    string? LicenseId,
    string? Customer,
    DateTimeOffset? ExpiresAt,
    int DaysRemaining,
    int MaxDevices,
    int MaxTags,
    string[] Features,
    DateTimeOffset? TrialEndsAt,
    int TrialDays)
{
    /// <summary>过期、无效或时钟异常时，除登录与激活外全部功能不可用。</summary>
    public bool Blocked => Status is LicenseStatus.Expired or LicenseStatus.Invalid or LicenseStatus.Tampered;
}

/// <summary>落盘状态：试用起点、激活码、最近一次运行时间（防时钟回拨）。</summary>
public sealed class LicenseStateFile
{
    public long TrialStartedAt { get; set; }
    public string? ActivationCode { get; set; }
    public long LastSeenUtc { get; set; }
    public bool ClockTampered { get; set; }
}

public sealed class LicenseOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>未激活时的试用天数，默认 365 天（一年）。</summary>
    public int TrialDays { get; set; } = 365;
    /// <summary>剩余天数低于该值时进入 ExpiringSoon，前端弹窗提醒。</summary>
    public int WarningDays { get; set; } = 15;
    /// <summary>允许的时钟回拨容差（秒）。</summary>
    public int ClockSkewSeconds { get; set; } = 300;
    /// <summary>公钥 PEM；留空时使用程序集内嵌公钥（推荐，防止替换公钥伪造授权）。</summary>
    public string? PublicKey { get; set; }
}