namespace Scada.Core.Licensing;

/// <summary>
/// 授权判定的纯函数部分：机器指纹比较、到期、剩余天数、时钟回拨。
///
/// 状态机（试用 / 激活 / 过期 / 篡改）、文案与落盘留在宿主里，因为它们涉及持久化和对外提示；
/// 但"什么算同一台机器""什么算过期"必须只有一处定义——原来这两段判断在
/// <c>LicenseService.Check</c> 与 <c>Refresh</c> 里各写了一遍，改一处漏一处就会让
/// "预览能激活、激活后又无效"这类问题重新出现。
/// </summary>
public static class LicenseEvaluation
{
    /// <summary>机器码比较前只保留字母数字：现场抄写时常带空格或多余的分隔符。</summary>
    public static string NormalizeMachineId(string machineId) =>
        new((machineId ?? "").Where(char.IsLetterOrDigit).ToArray());

    /// <summary>载荷不绑机器（空串）时视为匹配；否则按归一化后的机器码比较。</summary>
    public static bool MatchesMachine(LicensePayload payload, string machineId) =>
        string.IsNullOrWhiteSpace(payload.MachineId)
        || string.Equals(NormalizeMachineId(payload.MachineId), NormalizeMachineId(machineId), StringComparison.OrdinalIgnoreCase);

    public static bool IsExpired(LicensePayload payload, long nowUnix) => payload.ExpiresAt <= nowUnix;

    /// <summary>剩余天数向上取整，最少 0。到期当天仍算 0 天，与界面"剩余 N 天"一致。</summary>
    public static int DaysRemaining(long expiresUnix, long nowUnix) =>
        Math.Max(0, (int)Math.Ceiling((expiresUnix - nowUnix) / 86_400.0));

    /// <summary>时钟回拨：当前时间早于上次见到的时刻超过容忍值。</summary>
    public static bool IsClockRollback(long nowUnix, long lastSeenUnix, int skewSeconds) =>
        lastSeenUnix > 0 && nowUnix < lastSeenUnix - Math.Max(0, skewSeconds);

    /// <summary>剩余天数不超过预警窗口即视为即将到期（窗口至少 1 天）。</summary>
    public static bool IsExpiringSoon(int daysRemaining, int warningDays) =>
        daysRemaining <= Math.Max(1, warningDays);
}
