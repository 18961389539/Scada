using Scada.Desktop.Configuration;

namespace Scada.Desktop.Tests;

public sealed class DesktopLaunchErrorTests
{
    [Fact]
    public void WritesReadsAndClearsLaunchError()
    {
        var root = Path.Combine(Path.GetTempPath(), "scada-desktop-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new DesktopPaths(root);
            Assert.Null(DesktopLaunchError.Read(paths));
            DesktopLaunchError.Write(paths, "  该账号需要先修改初始密码  ");
            Assert.Equal("该账号需要先修改初始密码", DesktopLaunchError.Read(paths));
            Assert.True(File.Exists(paths.LastLaunchErrorFile));
            DesktopLaunchError.Clear(paths);
            Assert.Null(DesktopLaunchError.Read(paths));
            Assert.False(File.Exists(paths.LastLaunchErrorFile));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { /* 临时目录 */ }
        }
    }

    [Fact]
    public void SupervisorPromptDistinguishesMustChangePassword()
    {
        var must = DesktopLaunchError.SupervisorPrompt("该账号需要先修改初始密码，请先用浏览器登录并完成改密");
        Assert.Contains("修改初始密码", must);
        Assert.Contains("在本窗口修改初始密码", must);
        Assert.DoesNotContain("更新账号或显示目标", must);

        var other = DesktopLaunchError.SupervisorPrompt("用户名或密码错误");
        Assert.Contains("用户名或密码错误", other);
        Assert.Contains("是否打开设置检查后重试", other);
        Assert.DoesNotContain("更新账号或显示目标", other);

        var generic = DesktopLaunchError.SupervisorPrompt(null);
        Assert.Contains("自动登录或配置校验失败", generic);
        Assert.DoesNotContain("更新账号或显示目标", generic);
    }

    [Theory]
    [InlineData("账号已验证，需要先修改初始密码后才能继续。", true)]
    [InlineData("首次登录或密码已被重置，请先修改密码", true)]
    [InlineData("用户名或密码错误", false)]
    [InlineData(null, false)]
    public void DetectsMustChangePasswordMessage(string? message, bool expected)
    {
        Assert.Equal(expected, DesktopLaunchError.IsMustChangePassword(message));
    }
}
