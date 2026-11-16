using System.Text.Json;
using Scada.Desktop.Configuration;
using Scada.Tests;

namespace Scada.Desktop.Tests;

public sealed class ProvisionCommandTests
{
    [Fact]
    public void WritesUserConfigWithStdinPassword()
    {
        var directory = TempDirectory();
        var paths = new DesktopPaths(Path.Combine(directory, "user"), Path.Combine(directory, "machine"));
        paths.EnsureCreated();
        var configPath = Path.Combine(directory, "provision.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new MachineSettings
        {
            ServerUrl = "http://localhost:5080",
            UserName = "kiosk",
            Target = new DesktopTarget { Kind = "screen", ScreenId = 1 }
        }, MachineSettings.JsonOptions));

        var originalInput = Console.In;
        try
        {
            Console.SetIn(new StringReader("secret123"));
            // 测试进程通常已提权；显式允许，才能验证"写配置"本身（提权拒绝由 ShouldRefuseUserScope 覆盖）。
            var code = ProvisionCommand.Run(
                ["--provision", "--config", configPath, "--scope", "user", "--password-stdin", "--allow-elevated-user"], paths);
            Assert.Equal(0, code);

            var loaded = new SettingsStore(paths.SettingsFile).Load(out var error);
            Assert.Null(error);
            Assert.NotNull(loaded);
            Assert.Equal("http://localhost:5080", loaded!.ServerUrl);
            Assert.Equal("kiosk", loaded.UserName);
            Assert.Equal(1, loaded.Target.ScreenId);
            Assert.Equal("secret123", PasswordProtector.Unprotect(loaded.PasswordCipher));
            Assert.True(Guid.TryParse(loaded.ClientId, out _));
        }
        finally
        {
            Console.SetIn(originalInput);
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void RejectsPasswordForMachineScope()
    {
        var directory = TempDirectory();
        var paths = new DesktopPaths(Path.Combine(directory, "user"), Path.Combine(directory, "machine"));
        var configPath = Path.Combine(directory, "provision.json");
        File.WriteAllText(configPath, """{"serverUrl":"http://localhost:5080"}""");
        try
        {
            var code = ProvisionCommand.Run(
                ["--provision", "--config", configPath, "--scope", "machine", "--password-stdin"], paths);
            Assert.Equal(2, code);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    /// <summary>提权窗口里的用户级配准会写进管理员账户；默认拒绝，显式开关才允许。</summary>
    [Theory]
    [InlineData("user", true, false, true)]
    [InlineData("user", true, true, false)]
    [InlineData("user", false, false, false)]
    [InlineData("machine", true, false, false)]
    public void UserScopeRefusesElevatedWriteByDefault(string scope, bool elevated, bool allowElevated, bool expected) =>
        Assert.Equal(expected, ProvisionCommand.ShouldRefuseUserScope(scope, elevated, allowElevated));

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "scada-desktop-tests", Guid.NewGuid().ToString("N"));
        TempCleanup.Track(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* 测试清理失败不影响断言 */ }
    }
}
