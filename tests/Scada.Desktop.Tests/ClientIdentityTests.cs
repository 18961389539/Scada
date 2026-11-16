using Scada.Desktop.Configuration;
using Scada.Tests;

namespace Scada.Desktop.Tests;

public sealed class ClientIdentityTests
{
    [Fact]
    public void GeneratesAndPreservesClientId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scada-desktop-tests", Guid.NewGuid().ToString("N"));
        TempCleanup.Track(directory);
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            var settings = new DesktopSettings
            {
                ServerUrl = "http://localhost:5080",
                Target = new DesktopTarget { Kind = "screen", ScreenId = 1 }
            };

            var first = ClientIdentity.Ensure(settings, store);
            Assert.True(Guid.TryParse(first, out _));

            var loaded = store.Load(out var error);
            Assert.Null(error);
            Assert.NotNull(loaded);
            Assert.Equal(first, loaded!.ClientId);

            var second = ClientIdentity.Ensure(loaded!, store);
            Assert.Equal(first, second);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* 测试清理 */ }
        }
    }

    /// <summary>
    /// ClientId 缺失时只回写身份字段：调用方传进来的是合并机器配置后的对象，
    /// 整份保存会把管理员下发的地址与目标写进用户配置，撤掉机器配置后残留。
    /// </summary>
    [Fact]
    public void DoesNotWriteMachineLayerValuesIntoUserConfig()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scada-desktop-tests", Guid.NewGuid().ToString("N"));
        TempCleanup.Track(directory);
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SettingsStore(Path.Combine(directory, "settings.json"));
            store.Save(new DesktopSettings
            {
                ServerUrl = "http://user-host:5080",
                UserName = "user",
                Target = new DesktopTarget { Kind = "screen", ScreenId = 1 }
            });

            var merged = new DesktopSettings
            {
                ServerUrl = "http://machine-host:5080",
                UserName = "machine",
                Target = new DesktopTarget { Kind = "layout", LayoutId = 9 }
            };

            var clientId = ClientIdentity.Ensure(merged, store);

            var reloaded = store.Load(out var error);
            Assert.Null(error);
            Assert.NotNull(reloaded);
            Assert.Equal(clientId, reloaded!.ClientId);
            Assert.Equal("http://user-host:5080", reloaded.ServerUrl);
            Assert.Equal("user", reloaded.UserName);
            Assert.Equal("screen", reloaded.Target.Kind);
            Assert.Equal(1, reloaded.Target.ScreenId);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { /* 测试清理 */ }
        }
    }
}
