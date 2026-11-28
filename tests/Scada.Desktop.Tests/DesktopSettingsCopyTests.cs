using Scada.Desktop.Api;
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
    public void PasswordHintStopsSuggestingReuseWhenCipherIsStale()
    {
        Assert.Equal("尚未保存密码", DesktopSettingsCopy.PasswordHint(false, false, false, false));
        Assert.Equal("已保存密码，留空表示沿用", DesktopSettingsCopy.PasswordHint(true, false, false, false));
        Assert.Equal(DesktopSettingsCopy.MustChangePendingHint, DesktopSettingsCopy.PasswordHint(true, true, true, false));
        Assert.DoesNotContain("留空表示沿用", DesktopSettingsCopy.PasswordHint(true, true, true, false));
        Assert.Equal(DesktopSettingsCopy.SavedCipherStaleHint, DesktopSettingsCopy.PasswordHint(true, true, false, false));
        Assert.Equal(DesktopSettingsCopy.PasswordChangedHint, DesktopSettingsCopy.PasswordHint(true, true, false, true));
        Assert.DoesNotContain("留空表示沿用", DesktopSettingsCopy.PasswordChangedHint);
    }

    [Fact]
    public void LoginPageUrlAndBrowserHintIncludeReturnInstructions()
    {
        Assert.Equal("http://127.0.0.1:18927/login", DesktopSettingsCopy.LoginPageUrl("http://127.0.0.1:18927/"));
        var hint = DesktopSettingsCopy.BrowserHint("http://127.0.0.1:18927/login");
        Assert.Contains("http://127.0.0.1:18927/login", hint);
        Assert.Contains("回到本窗口输入新密码", hint);
        Assert.Contains("取消置顶", hint);
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
    public void WorkspaceHintExplainsNoScreenSelection()
    {
        Assert.Contains("不需要再选画面或布局", DesktopSettingsCopy.WorkspaceHint);
        Assert.Contains("保存并启动", DesktopSettingsCopy.WorkspaceHint);
        Assert.Equal(LoginResult.MustChangePasswordMessage, DesktopSettingsCopy.MustChangeLoginStatus);
    }
}
