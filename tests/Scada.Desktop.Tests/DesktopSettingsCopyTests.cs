using Scada.Desktop.Api;
using Scada.Desktop.Configuration;
using Scada.Desktop.Ui;

namespace Scada.Desktop.Tests;

public sealed class DesktopSettingsCopyTests
{
    [Fact]
    public void NoticeAndAccountHintDistinguishWorkspaceAndDemo()
    {
        var normal = DesktopSettingsCopy.Notice(null, false);
        Assert.Contains("现场只看的专用账号", normal);
        Assert.Contains("多页面工作台", normal);
        Assert.DoesNotContain("演示模式", normal);

        var demo = DesktopSettingsCopy.Notice("SCADA Customer Demo", true);
        Assert.Contains("SCADA Customer Demo", demo);
        Assert.Contains("演示模式", demo);
        Assert.Contains("仍需先修改初始密码", DesktopSettingsCopy.DemoPasswordHint);

        Assert.Contains("多页面工作台", DesktopSettingsCopy.AccountHint("workspace"));
        Assert.Contains("工程或管理员账号", DesktopSettingsCopy.AccountHint("workspace"));
        Assert.Contains("现场只看", DesktopSettingsCopy.AccountHint("screen"));
    }

    [Fact]
    public void PasswordHintStopsSuggestingReuseWhenCipherIsStaleOrRejected()
    {
        Assert.Equal("尚未保存密码", DesktopSettingsCopy.PasswordHint(false, false, false, false));
        Assert.Equal("已保存密码，留空表示沿用", DesktopSettingsCopy.PasswordHint(true, false, false, false));
        Assert.Equal(DesktopSettingsCopy.MustChangePendingHint, DesktopSettingsCopy.PasswordHint(true, false, true, false));
        Assert.Contains("仍可使用", DesktopSettingsCopy.MustChangePendingHint);
        Assert.DoesNotContain("不能沿用", DesktopSettingsCopy.MustChangePendingHint);
        Assert.DoesNotContain("留空表示沿用", DesktopSettingsCopy.PasswordHint(true, false, true, false));
        Assert.Equal(DesktopSettingsCopy.SavedCipherStaleHint, DesktopSettingsCopy.PasswordHint(true, true, false, false));
        Assert.Equal(DesktopSettingsCopy.PasswordChangedHint, DesktopSettingsCopy.PasswordHint(true, true, false, true));
        Assert.Contains("保存并启动", DesktopSettingsCopy.PasswordChangedHint);
        Assert.DoesNotContain("留空表示沿用", DesktopSettingsCopy.PasswordChangedHint);
        Assert.Equal(DesktopSettingsCopy.CredentialsRejectedHint, DesktopSettingsCopy.PasswordHint(true, false, false, false, true));
        Assert.DoesNotContain("留空表示沿用", DesktopSettingsCopy.CredentialsRejectedHint);
        Assert.Contains("重新输入", DesktopSettingsCopy.CredentialsRejectedHint);
    }

    [Fact]
    public void LoginPageUrlAndBrowserHintIncludeReturnInstructions()
    {
        Assert.Equal("http://127.0.0.1:18927/login", DesktopSettingsCopy.LoginPageUrl("http://127.0.0.1:18927/"));
        var hint = DesktopSettingsCopy.BrowserHint("http://127.0.0.1:18927/login");
        Assert.Contains("http://127.0.0.1:18927/login", hint);
        Assert.Contains("输入新密码后再登录并保存", hint);
        Assert.Contains(DesktopSettingsCopy.BrowserDoneButton, hint);
        Assert.Contains("不会置顶", hint);
    }

    [Fact]
    public void ValidateNewPasswordMatchesServerRules()
    {
        Assert.Equal("请输入当前密码", DesktopSettingsCopy.ValidateNewPassword("", "abcdef", "abcdef"));
        Assert.Equal("新密码至少需要 6 个字符", DesktopSettingsCopy.ValidateNewPassword("old", "abc", "abc"));
        Assert.Equal("两次输入的新密码不一致", DesktopSettingsCopy.ValidateNewPassword("old", "abcdef", "abcdeg"));
        Assert.Equal("新密码不能与当前密码相同", DesktopSettingsCopy.ValidateNewPassword("abcdef", "abcdef", "abcdef"));
        Assert.Null(DesktopSettingsCopy.ValidateNewPassword("oldpass", "newpass", "newpass"));
    }

    [Fact]
    public void WorkspaceAndDemoHintsDependOnMustChangePending()
    {
        Assert.Contains("不需要再选画面或布局", DesktopSettingsCopy.WorkspaceHintText(false));
        Assert.Contains("登录后即可保存并启动", DesktopSettingsCopy.WorkspaceHintText(false));
        Assert.DoesNotContain("改密", DesktopSettingsCopy.WorkspaceHintText(false));
        Assert.Contains("改密并登录后即可保存并启动", DesktopSettingsCopy.WorkspaceHintText(true));
        Assert.False(DesktopSettingsCopy.ShowDemoPasswordHint(true, false));
        Assert.True(DesktopSettingsCopy.ShowDemoPasswordHint(true, true));
        Assert.False(DesktopSettingsCopy.ShowDemoPasswordHint(false, true));
        Assert.Equal(LoginResult.MustChangePasswordMessage, DesktopSettingsCopy.MustChangeLoginStatus);
    }

    [Fact]
    public void SavedCipherCanBeReusedUntilChangeSucceedsOrCredentialsAreRejected()
    {
        Assert.True(DesktopSettingsCopy.CanReuseSavedCipher(false, false));
        Assert.False(DesktopSettingsCopy.CanReuseSavedCipher(true, false));
        Assert.False(DesktopSettingsCopy.CanReuseSavedCipher(false, true));
        Assert.False(DesktopSettingsCopy.CanReuseSavedCipher(true, true));
    }

    [Fact]
    public void CancelAfterUnsavedServerPasswordChangeDoesNotRestartClient()
    {
        Assert.True(DesktopSettingsCopy.ShouldStartClientAfterSettings(true, true, false));
        Assert.True(DesktopSettingsCopy.ShouldStartClientAfterSettings(false, true, false));
        Assert.True(DesktopSettingsCopy.ShouldStartClientAfterSettings(true, false, false));
        Assert.False(DesktopSettingsCopy.ShouldStartClientAfterSettings(true, false, true));
        Assert.False(DesktopSettingsCopy.ShouldStartClientAfterSettings(false, false, true));
        Assert.False(DesktopSettingsCopy.ShouldStartClientAfterSettings(false, false, false));
        Assert.Contains("尚未保存到本机", DesktopSettingsCopy.UnsavedPasswordCloseMessage);
        Assert.Contains("要保存并启动吗", DesktopSettingsCopy.UnsavedPasswordCloseMessage);
    }

    [Fact]
    public void MustChangeLaunchErrorOpensSettingsWithoutTopMost()
    {
        Assert.True(DesktopSettingsCopy.ShouldOpenWithoutTopMost("该账号需要先修改初始密码"));
        Assert.False(DesktopSettingsCopy.ShouldOpenWithoutTopMost("用户名或密码错误"));
        Assert.False(DesktopSettingsCopy.ShouldOpenWithoutTopMost(null));
    }

    [Fact]
    public void TargetListLoadStatusIsSuccessWhenScreensExistAndKindDoesNotNeedLayouts()
    {
        foreach (var kind in new[] { "workspace", "screen", "playlist" })
        {
            var status = DesktopSettingsCopy.DescribeTargetListLoad(kind, 3, 0, 0, 0);
            Assert.False(status.IsError);
            Assert.Equal("已加载 3 个画面、0 个布局、0 个播放列表、0 个工作站。", status.Message);
            Assert.DoesNotContain("当前账号看不到", status.Message);
            Assert.DoesNotContain("可使用单个画面模式", status.Message);
        }
    }

    [Fact]
    public void TargetListLoadStatusErrorsOnlyWhenCurrentKindNeedsMissingLayoutsOrWorkstations()
    {
        var layout = DesktopSettingsCopy.DescribeTargetListLoad("layout", 3, 0, 0, 0);
        Assert.True(layout.IsError);
        Assert.Contains("还没有已发布的运行布局", layout.Message);
        Assert.Contains("请先在网页中创建并发布", layout.Message);
        Assert.DoesNotContain("当前账号看不到", layout.Message);

        var station = DesktopSettingsCopy.DescribeTargetListLoad("workstation", 3, 0, 0, 0);
        Assert.True(station.IsError);
        Assert.Contains("还没有工作站", station.Message);
        Assert.Contains("请先在网页中", station.Message);
        Assert.DoesNotContain("当前账号看不到", station.Message);

        var layoutOk = DesktopSettingsCopy.DescribeTargetListLoad("layout", 0, 2, 0, 0);
        Assert.False(layoutOk.IsError);
        Assert.Equal("已加载 0 个画面、2 个布局、0 个播放列表、0 个工作站。", layoutOk.Message);

        var stationOk = DesktopSettingsCopy.DescribeTargetListLoad("workstation", 3, 1, 0, 2);
        Assert.False(stationOk.IsError);
    }

    [Fact]
    public void TargetListLoadStatusFollowsKindSwitchAndStillWarnsWhenScreenKindHasNoScreens()
    {
        var missingScreensOnWorkspace = DesktopSettingsCopy.DescribeTargetListLoad("workspace", 0, 0, 0, 0);
        Assert.False(missingScreensOnWorkspace.IsError);
        Assert.Equal("已加载 0 个画面、0 个布局、0 个播放列表、0 个工作站。", missingScreensOnWorkspace.Message);

        var screen = DesktopSettingsCopy.DescribeTargetListLoad("screen", 0, 0, 0, 0);
        Assert.True(screen.IsError);
        Assert.Contains("还没有已发布的组态画面", screen.Message);
        Assert.DoesNotContain("当前账号看不到", screen.Message);

        var afterSwitch = DesktopSettingsCopy.DescribeTargetListLoad("screen", 3, 0, 0, 0);
        Assert.False(afterSwitch.IsError);
        var backToLayout = DesktopSettingsCopy.DescribeTargetListLoad("layout", 3, 0, 0, 0);
        Assert.True(backToLayout.IsError);
        var backToWorkspace = DesktopSettingsCopy.DescribeTargetListLoad("workspace", 3, 0, 0, 0);
        Assert.False(backToWorkspace.IsError);
    }
}
