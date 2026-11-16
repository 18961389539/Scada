namespace Scada.Desktop.Configuration;

internal static class SettingsValidator
{
    public static readonly string[] TargetKinds = ["screen", "layout", "workstation", "workspace"];

    /// <summary>
    /// 规范化服务器地址：允许省略 scheme，但只接受 http/https 根地址，防止误填到子路径后
    /// 前端 history 路由和 /api 请求全部落到错误位置。
    /// </summary>
    public static bool TryNormalizeServerUrl(string? raw, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var text = raw?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "请填写服务器地址";
            return false;
        }
        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri is null)
        {
            error = "服务器地址格式不正确，例如 http://192.168.1.10:5080";
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            error = "服务器地址只支持 http 或 https";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "服务器地址不能包含用户名或密码";
            return false;
        }
        if (uri.AbsolutePath is not ("" or "/"))
        {
            error = "服务器地址只能是根路径，例如 http://192.168.1.10:5080";
            return false;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "服务器地址不能包含查询参数或锚点";
            return false;
        }
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    /// <summary>运行前校验。会顺手把服务器地址和目标类型规范化，避免大小写/尾斜杠差异。</summary>
    public static string? Validate(DesktopSettings settings, bool requirePassword)
    {
        if (settings.Version > 1) return "配置文件版本高于当前客户端版本，请升级客户端";
        if (!TryNormalizeServerUrl(settings.ServerUrl, out var url, out var error)) return error;
        settings.ServerUrl = url;

        settings.Target.Kind = (settings.Target.Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!TargetKinds.Contains(settings.Target.Kind)) return "请选择要显示的组态画面、运行布局或工作台";

        switch (settings.Target.Kind)
        {
            case "screen" when settings.Target.ScreenId is not > 0:
                return "请选择已发布的组态画面";
            case "layout" when settings.Target.LayoutId is not > 0:
                return "请选择已发布的运行布局";
            case "workstation" when settings.Target.WorkstationId is not > 0:
                return "请选择工作站";
        }

        if (requirePassword && settings.SavePassword && string.IsNullOrWhiteSpace(settings.PasswordCipher))
        {
            return "请填写密码，或取消“保存密码”后由操作员手动登录";
        }
        return null;
    }
}
