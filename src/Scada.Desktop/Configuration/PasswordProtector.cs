using System.Security.Cryptography;
using System.Text;

namespace Scada.Desktop.Configuration;

/// <summary>
/// 用 Windows DPAPI 按当前用户加密专用账号密码。密文只能在同一 Windows 用户下解开，
/// 配置文件被拷走或换用户登录都无法直接得到明文。
/// </summary>
internal static class PasswordProtector
{
    public static string Protect(string password)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(password);
        var cipher = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>解密失败（换用户、配置损坏）时返回 null，由调用方提示重新输入密码。</summary>
    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrWhiteSpace(cipher)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }
}
