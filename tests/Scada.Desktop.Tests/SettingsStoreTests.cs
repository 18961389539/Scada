using Scada.Desktop.Configuration;
using Scada.Tests;

namespace Scada.Desktop.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void SavesAndLoadsSettings()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var settings = ValidSettings();
            store.Save(settings);

            var loaded = store.Load(out var error);
            Assert.Null(error);
            Assert.NotNull(loaded);
            Assert.Equal("http://localhost:5080", loaded!.ServerUrl);
            Assert.Equal("kiosk01", loaded.UserName);
            Assert.Equal(12, loaded.Target.ScreenId);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CreatesBackupOnReplacement()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(ValidSettings());
            store.Save(ValidSettings());
            Assert.True(File.Exists(store.Path + ".bak"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void MissingFileReportsNotConfigured()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            Assert.Null(store.Load(out var error));
            Assert.Contains("尚未配置", error);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void CorruptFileIsBackedUp()
    {
        var directory = TempDirectory();
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            File.WriteAllText(store.Path, "{not valid json");
            Assert.Null(store.Load(out var error));
            Assert.NotNull(error);
            Assert.Single(Directory.GetFiles(directory, "settings.json.corrupt-*"));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static DesktopSettings ValidSettings() => new()
    {
        ServerUrl = "http://localhost:5080",
        UserName = "kiosk01",
        PasswordCipher = PasswordProtector.Protect("secret"),
        SavePassword = true,
        Target = new DesktopTarget { Kind = "screen", ScreenId = 12 },
        Display = new DesktopDisplay { FullScreen = true }
    };

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
