using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scada.Desktop.Configuration;

/// <summary>
/// 机器级配置：只放连接目标、显示与非机密偏好，由安装器/管理员写入 ProgramData。
/// 账号密码永远留在用户配置里，避免 DPAPI 跨账户不可解密。
/// </summary>
internal sealed class MachineSettings
{
    public int Version { get; set; } = 1;

    public string? ServerUrl { get; set; }

    public string? UserName { get; set; }

    public DesktopTarget? Target { get; set; }

    public DesktopDisplay? Display { get; set; }

    public bool? AutoStart { get; set; }

    public bool? AllowDownloads { get; set; }

    public string? DownloadDirectory { get; set; }

    /// <summary>管理员要锁定的字段名；为空时按已下发的字段自动锁定。</summary>
    public string[] Locked { get; set; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class InstallManifest
{
    public string? ManagedAutostart { get; set; }
    public string? Version { get; set; }
}
