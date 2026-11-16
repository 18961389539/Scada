using Scada.Desktop.Configuration;

namespace Scada.Desktop.Tests;

public sealed class DesktopConfigLayersTests
{
    [Fact]
    public void LockedFieldsKeepUserLayerValues()
    {
        var user = new DesktopSettings
        {
            ServerUrl = "http://user-host:5080",
            UserName = "user",
            Target = new DesktopTarget { Kind = "screen", ScreenId = 1 },
            AutoStart = false,
            AllowDownloads = false
        };
        var edited = new DesktopSettings
        {
            ServerUrl = "http://machine-host:5080",
            UserName = "machine",
            Target = new DesktopTarget { Kind = "layout", LayoutId = 9 },
            AutoStart = true,
            AllowDownloads = true,
            DownloadDirectory = @"D:\exports"
        };

        DesktopConfigLayers.ApplyEditableFields(user, edited, ["serverUrl", "target", "autoStart"]);

        Assert.Equal("http://user-host:5080", user.ServerUrl);
        Assert.Equal("machine", user.UserName);
        Assert.Equal("screen", user.Target.Kind);
        Assert.Equal(1, user.Target.ScreenId);
        Assert.False(user.AutoStart);
        Assert.True(user.AllowDownloads);
        Assert.Equal(@"D:\exports", user.DownloadDirectory);
    }

    [Fact]
    public void PasswordAndIdentityAlwaysFollowEdited()
    {
        var user = new DesktopSettings { PasswordCipher = "old", SavePassword = false, KeepAwake = true, ClientId = "old-id" };
        var edited = new DesktopSettings { PasswordCipher = "new", SavePassword = true, KeepAwake = false, ClientId = "new-id" };

        DesktopConfigLayers.ApplyEditableFields(user, edited, ["serverUrl", "userName", "target", "display", "autoStart", "allowDownloads", "downloadDirectory"]);

        Assert.Equal("new", user.PasswordCipher);
        Assert.True(user.SavePassword);
        Assert.False(user.KeepAwake);
        Assert.Equal("new-id", user.ClientId);
    }

    [Fact]
    public void LockNamesAreCaseInsensitive()
    {
        var user = new DesktopSettings { ServerUrl = "http://user-host:5080" };
        var edited = new DesktopSettings { ServerUrl = "http://machine-host:5080" };

        DesktopConfigLayers.ApplyEditableFields(user, edited, ["SERVERURL"]);

        Assert.Equal("http://user-host:5080", user.ServerUrl);
    }
}
