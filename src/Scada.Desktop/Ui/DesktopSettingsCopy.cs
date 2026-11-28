using Scada.Desktop.Api;
using Scada.Desktop.Configuration;

namespace Scada.Desktop.Ui;

/// <summary>设置页与改密流程的文案，抽出来便于专项测试，不改变强制首登改密策略。</summary>
internal static class DesktopSettingsCopy
{
    public const string MustChangeLoginStatus = LoginResult.MustChangePasswordMessage;

    public const string DemoPasswordHint = "当前是演示站点。文档中的示例账号 admin / admin123 仍需先修改初始密码，可在本窗口完成改密后再保存并启动。";

    public const string SavedCipherStaleHint = "请填写并保存新密码，已保存的旧密码已不能再用。";

    public const string MustChangePendingHint = "该账号需要先改密。可在下方填写新密码，或打开浏览器改密。改密前已保存的当前密码仍可使用。";

    public const string PasswordChangedHint = "密码已更新，请先点“保存并启动”，把新密码写入本机。请勿留空，以免沿用旧密码。";

    public const string CredentialsRejectedHint = "请重新输入密码。不要留空沿用刚才失败的已保存密码。";

    public const string AwaitingNewPasswordHint = "请在上方密码框输入新密码，再点“登录并加载列表”，然后保存并启动。不要留空沿用旧密码。";

    public const string BrowserDoneButton = "已在浏览器完成改密，请输入新密码后登录";

    public const string UnsavedPasswordCloseMessage = "新密码已在服务器生效，但尚未保存到本机。若不保存，下次仍会用旧密码登录并失败。\n\n要保存并启动吗？";

    public static string WorkspaceHintText(bool mustChangePending) =>
        mustChangePending
            ? "工作台不需要再选画面或布局，改密并登录后即可保存并启动。"
            : "工作台不需要再选画面或布局，登录后即可保存并启动。";

    public static string Notice(string? siteName, bool demoMode)
    {
        var body = "这页既可以配置现场只看的专用账号（建议只授予查看权限），也可以用工程或管理员账号登录多页面工作台。密码只以 Windows DPAPI 加密保存在本机，不会写入日志。";
        if (!demoMode) return body;
        var site = string.IsNullOrWhiteSpace(siteName) ? "演示站点" : siteName.Trim();
        return $"当前站点：{site}（演示模式）。{body}";
    }

    public static string AccountHint(string kind) =>
        kind == "workspace"
            ? "当前目标是多页面工作台，可使用工程或管理员账号登录。"
            : "当前目标更适合现场只看的专用账号；若需禁止下发，请授予查看权限或启用监视模式。";

    public static string PasswordHint(bool hasSavedCipher, bool savedCipherStale, bool mustChangePending, bool passwordChanged, bool rejectSavedCipher = false)
    {
        if (passwordChanged) return PasswordChangedHint;
        if (savedCipherStale) return SavedCipherStaleHint;
        if (rejectSavedCipher) return CredentialsRejectedHint;
        if (mustChangePending) return MustChangePendingHint;
        return hasSavedCipher ? "已保存密码，留空表示沿用" : "尚未保存密码";
    }

    public static string LoginPageUrl(string serverUrl)
    {
        var root = (serverUrl ?? string.Empty).Trim().TrimEnd('/');
        return string.IsNullOrEmpty(root) ? string.Empty : root + "/login";
    }

    public static string BrowserHint(string loginUrl) =>
        $"浏览器地址：{loginUrl}\n在浏览器中登录并改密后，请回到本窗口，点“{BrowserDoneButton}”，输入新密码后再登录并保存。窗口不会置顶，以免挡住浏览器。";

    public static bool ShowDemoPasswordHint(bool demoMode, bool mustChangePending) =>
        demoMode && mustChangePending;

    public static bool CanReuseSavedCipher(bool savedCipherStale, bool rejectSavedCipher) =>
        !savedCipherStale && !rejectSavedCipher;

    public static bool ShouldStartClientAfterSettings(bool restartAfter, bool saved, bool unsavedServerPasswordChange)
    {
        if (saved) return true;
        if (unsavedServerPasswordChange) return false;
        return restartAfter;
    }

    public static bool ShouldOpenWithoutTopMost(string? initialError) =>
        DesktopLaunchError.IsMustChangePassword(initialError);

    public static string? ValidateNewPassword(string currentPassword, string newPassword, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(currentPassword)) return "请输入当前密码";
        if (string.IsNullOrWhiteSpace(newPassword)) return "请输入新密码";
        if (newPassword.Length < 6) return "新密码至少需要 6 个字符";
        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal)) return "两次输入的新密码不一致";
        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal)) return "新密码不能与当前密码相同";
        return null;
    }
}
