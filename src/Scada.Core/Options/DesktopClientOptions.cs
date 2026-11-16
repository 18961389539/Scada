namespace Scada.Core.Options;

/// <summary>
/// 桌面客户端集中运维配置。版本为空表示不做限制；下载地址由客户端校验为 http/https。
/// </summary>
public sealed class DesktopClientOptions
{
    public string? MinimumVersion { get; set; }

    public string? LatestVersion { get; set; }

    public string? DownloadUrl { get; set; }

    /// <summary>服务端下发给客户端的心跳周期（秒），有效范围 10–600。客户端按此值重排定时器。</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    // 3 个心跳周期未上报即视为离线。
    public int OnlineThresholdSeconds { get; set; } = 90;
}
