namespace Scada.Core.Versioning;

/// <summary>
/// 客户端版本比较：只取前四段数字，预发布后缀（-beta）与构建元数据（+sha）忽略。
///
/// 服务端算"是否必须升级"、客户端算"是否被门禁挡住"必须是同一套规则，否则两边对
/// 同一个版本号会给出不同结论。此前 Desktop 与 Server 各写了一份，这里统一。
/// 无法解析时一律返回 false（不限制）：现场宁可先能看画面，也不因为格式问题锁死终端。
/// </summary>
public static class AppVersionRule
{
    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        var suffix = text.IndexOfAny(['-', '+']);
        if (suffix >= 0) text = text[..suffix];
        var parts = text.Split('.');
        if (parts.Length is 0 or > 4) return false;
        for (var count = parts.Length; count < 4; count++) text += ".0";
        return Version.TryParse(text, out version!);
    }

    public static bool IsBelow(string? current, string? required) =>
        TryParse(current, out var currentVersion) && IsBelow(currentVersion, required);

    public static bool IsBelow(Version current, string? required) =>
        !string.IsNullOrWhiteSpace(required)
        && TryParse(required, out var requiredVersion)
        && current < requiredVersion;
}
