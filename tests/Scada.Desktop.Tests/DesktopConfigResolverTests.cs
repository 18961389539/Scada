using System.Text.Json;
using Scada.Desktop.Configuration;

namespace Scada.Desktop.Tests;

public sealed class DesktopConfigResolverTests
{
    [Fact]
    public void MachineValuesOverrideUserValues()
    {
        var user = new DesktopSettings
        {
            ServerUrl = "http://user:5080",
            UserName = "kiosk",
            Target = new DesktopTarget { Kind = "screen", ScreenId = 1 },
            AutoStart = false
        };
        var machine = new MachineSettings
        {
            ServerUrl = "http://machine:5080",
            UserName = "line-operator",
            Target = new DesktopTarget { Kind = "layout", LayoutId = 2, TargetName = "总览" },
            AutoStart = true,
            AllowDownloads = true,
            Locked = ["target", "serverUrl"]
        };

        var resolved = DesktopConfigResolver.Resolve(user, machine, managedAutostart: false);

        Assert.NotNull(resolved);
        Assert.Null(resolved!.Error);
        Assert.Equal("http://machine:5080", resolved.Settings.ServerUrl);
        Assert.Equal("line-operator", resolved.Settings.UserName);
        Assert.Equal("layout", resolved.Settings.Target.Kind);
        Assert.Equal(2, resolved.Settings.Target.LayoutId);
        Assert.True(resolved.Settings.AutoStart);
        Assert.True(resolved.Settings.AllowDownloads);
        Assert.Equal("machine", resolved.Settings.ConfigScope);
        Assert.Contains("target", resolved.Settings.LockedFields);
        Assert.False(resolved.Settings.ManagedAutostart);
    }

    [Fact]
    public void DefaultsLocksToProvidedMachineFields()
    {
        var machine = new MachineSettings
        {
            ServerUrl = "http://machine:5080",
            Target = new DesktopTarget { Kind = "screen", ScreenId = 3 },
            Display = new DesktopDisplay { FullScreen = true },
            AutoStart = true,
            AllowDownloads = false
        };

        var resolved = DesktopConfigResolver.Resolve(null, machine, managedAutostart: true);

        Assert.NotNull(resolved);
        Assert.True(resolved!.Settings.ManagedAutostart);
        Assert.Contains("serverUrl", resolved.Settings.LockedFields);
        Assert.Contains("target", resolved.Settings.LockedFields);
        Assert.Contains("display", resolved.Settings.LockedFields);
        Assert.Contains("autoStart", resolved.Settings.LockedFields);
        Assert.Contains("allowDownloads", resolved.Settings.LockedFields);
    }

    [Fact]
    public void ReportsValidationErrorButKeepsBaseline()
    {
        var machine = new MachineSettings { ServerUrl = "http://machine:5080" };
        var resolved = DesktopConfigResolver.Resolve(null, machine, managedAutostart: false);
        Assert.NotNull(resolved);
        Assert.NotNull(resolved!.Error);
        Assert.Equal("http://machine:5080", resolved.Settings.ServerUrl);
    }

    [Fact]
    public void ReturnsNullWhenNothingConfigured()
    {
        Assert.Null(DesktopConfigResolver.Resolve(null, null, managedAutostart: false));
    }

    [Fact]
    public void UserOnlyConfigRemainsCompatible()
    {
        var user = new DesktopSettings
        {
            ServerUrl = "http://user:5080",
            Target = new DesktopTarget { Kind = "screen", ScreenId = 5 }
        };

        var resolved = DesktopConfigResolver.Resolve(user, null, managedAutostart: false);

        Assert.NotNull(resolved);
        Assert.Null(resolved!.Error);
        Assert.Equal("user", resolved.Settings.ConfigScope);
        Assert.Equal(5, resolved.Settings.Target.ScreenId);
        Assert.Empty(resolved.Settings.LockedFields);
    }

    /// <summary>手写/脚本生成的机器配置可能显式写 "locked": null，不能因此让客户端启动崩溃。</summary>
    [Fact]
    public void NullLockedListFallsBackToDefaultLocks()
    {
        var machine = JsonSerializer.Deserialize<MachineSettings>(
            """{"serverUrl":"http://machine:5080","locked":null}""", MachineSettings.JsonOptions)!;
        Assert.Null(machine.Locked);

        var resolved = DesktopConfigResolver.Resolve(null, machine, managedAutostart: false);

        Assert.NotNull(resolved);
        Assert.Contains("serverUrl", resolved!.Settings.LockedFields);
    }

    [Fact]
    public void CorruptMachineFileFallsBackWithError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scada-desktop-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "client.json");
            File.WriteAllText(path, "{not json");
            var machine = new MachineSettingsStore(path).Load(out var error);
            Assert.Null(machine);
            Assert.NotNull(error);
            Assert.Contains("机器配置", error);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* 测试清理 */ }
        }
    }
}
