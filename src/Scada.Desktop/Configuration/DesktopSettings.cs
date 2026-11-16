using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scada.Desktop.Configuration;

internal sealed class DesktopSettings
{
    public int Version { get; set; } = 1;

    public string ServerUrl { get; set; } = "http://localhost:5080";

    public string? UserName { get; set; }

    /// <summary>安装后保持稳定；用于服务端在线去重，重装/重新配准时不能重建。</summary>
    public string? ClientId { get; set; }

    /// <summary>DPAPI 加密后的密码；空表示不自动登录。</summary>
    public string? PasswordCipher { get; set; }

    public bool SavePassword { get; set; } = true;

    public DesktopTarget Target { get; set; } = new();

    public DesktopDisplay Display { get; set; } = new();

    public bool KeepAwake { get; set; } = true;

    public bool AutoStart { get; set; }

    public bool AllowDownloads { get; set; }

    public string? DownloadDirectory { get; set; }

    /// <summary>机器配置锁定的字段，仅运行时使用，不写入用户配置文件。</summary>
    [JsonIgnore]
    public string[] LockedFields { get; set; } = [];

    /// <summary>开机自启是否由安装器的计划任务托管，仅运行时使用。</summary>
    [JsonIgnore]
    public bool ManagedAutostart { get; set; }

    /// <summary>machine / user / local，仅运行时用于心跳展示。</summary>
    [JsonIgnore]
    public string? ConfigScope { get; set; }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class DesktopTarget
{
    /// <summary>screen / layout / workstation / workspace。</summary>
    public string Kind { get; set; } = "screen";

    public int? ScreenId { get; set; }

    public int? LayoutId { get; set; }

    public int? WorkstationId { get; set; }

    public string? WorkstationName { get; set; }

    /// <summary>仅用于心跳/运维展示；不参与导航。</summary>
    public string? TargetName { get; set; }

    public bool MonitorOnly { get; set; } = true;
}

internal sealed class DesktopDisplay
{
    public bool FullScreen { get; set; } = true;

    /// <summary>Screen.DeviceName，例如 \\.\DISPLAY1；为空表示主显示器。</summary>
    public string? MonitorDeviceName { get; set; }
}
