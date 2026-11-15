using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scada.Desktop.Configuration;

internal sealed class DesktopSettings
{
    public int Version { get; set; } = 1;

    public string ServerUrl { get; set; } = "http://localhost:5080";

    public string? UserName { get; set; }

    /// <summary>DPAPI 加密后的密码；空表示不自动登录。</summary>
    public string? PasswordCipher { get; set; }

    public bool SavePassword { get; set; } = true;

    public DesktopTarget Target { get; set; } = new();

    public DesktopDisplay Display { get; set; } = new();

    public bool KeepAwake { get; set; } = true;

    public bool AutoStart { get; set; }

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class DesktopTarget
{
    /// <summary>screen / layout / workstation。</summary>
    public string Kind { get; set; } = "screen";

    public int? ScreenId { get; set; }

    public int? LayoutId { get; set; }

    public int? WorkstationId { get; set; }

    public string? WorkstationName { get; set; }

    public bool MonitorOnly { get; set; } = true;
}

internal sealed class DesktopDisplay
{
    public bool FullScreen { get; set; } = true;

    /// <summary>Screen.DeviceName，例如 \\.\DISPLAY1；为空表示主显示器。</summary>
    public string? MonitorDeviceName { get; set; }
}
