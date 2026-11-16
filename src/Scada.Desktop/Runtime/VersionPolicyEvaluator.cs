using Scada.Core.Versioning;
using Scada.Desktop.Api;

namespace Scada.Desktop.Runtime;

internal enum VersionGate
{
    Unknown,
    Ok,
    UpdateAvailable,
    Block
}

internal static class VersionPolicyEvaluator
{
    public static VersionGate Evaluate(string? currentVersion, VersionPolicy? policy)
    {
        if (policy is null) return VersionGate.Unknown;
        if (!AppVersionRule.TryParse(currentVersion, out var current))
        {
            // 版本解析失败时 fail-open：宁可让现场先能看画面，也不要因为格式问题锁死终端。
            return VersionGate.Unknown;
        }

        if (policy.UpdateRequired || AppVersionRule.IsBelow(current, policy.MinimumVersion)) return VersionGate.Block;
        if (policy.UpdateAvailable || AppVersionRule.IsBelow(current, policy.LatestVersion)) return VersionGate.UpdateAvailable;
        if (string.IsNullOrWhiteSpace(policy.MinimumVersion) && string.IsNullOrWhiteSpace(policy.LatestVersion))
            return VersionGate.Unknown;
        return VersionGate.Ok;
    }

    /// <summary>规则在 <see cref="AppVersionRule"/>：服务端算"必须升级"用的是同一份实现。</summary>
    public static bool IsBelow(string currentVersion, string? requiredVersion) =>
        AppVersionRule.IsBelow(currentVersion, requiredVersion);
}
