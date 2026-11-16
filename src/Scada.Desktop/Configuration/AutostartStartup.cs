namespace Scada.Desktop.Configuration;

/// <summary>
/// 启动阶段应用开机自启。失败只写一条告警日志，不阻塞画面启动。
///
/// 单独抽出来是为了让"失败必须落日志"有回归用例：此前 Program.cs 写成
/// <c>Apply(...) &amp;&amp; warning != null</c>，短路后失败分支一条日志都没有，
/// 现场只能看到"重启后没自启"。
/// </summary>
internal static class AutostartStartup
{
    public static void Apply(DesktopSettings settings) =>
        Apply(settings, AutostartRegistration.Apply, message => DesktopLogger.Warn("开机自启未生效：" + message));

    /// <summary>可注入的入口，供单测替换注册表写入。</summary>
    public static void Apply(
        DesktopSettings settings,
        Func<bool, bool, AutostartResult> apply,
        Action<string> warn)
    {
        var result = apply(settings.AutoStart, settings.ManagedAutostart);
        if (result.Warning is { Length: > 0 } warning) warn(warning);
    }
}
