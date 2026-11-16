using System.Reflection;
using Microsoft.Extensions.Logging;
using Scada.Core.Options;
using Scada.Core.Security;

namespace Scada.Server.Startup;

/// <summary>
/// 启动期把"这次部署到底改了什么"写进日志。
///
/// <see cref="ScadaSettings"/> 有 120 多个配置项，appsettings.json 里只列了不到一半，
/// 其余全靠代码默认值——运维既看不到，也不知道能改。排查"为什么这台机器行为和那台不一样"时，
/// 只能逐个键去猜。这里把与默认值不同的项列出来（敏感项跳过），一行就能对齐差异。
/// </summary>
internal static class ScadaStartupLog
{
    public static void NonDefaultSettings(ILogger logger, ScadaSettings settings)
    {
        var defaults = new ScadaSettings();
        var changed = new List<string>();

        foreach (var property in typeof(ScadaSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead) continue;
            // 令牌、口令、连接串里的密钥不落日志：Sensitive 特性是审计脱敏用的同一个标记。
            if (property.GetCustomAttribute<SensitiveAttribute>() is not null) continue;
            // 只比标量。数组/集合/嵌套对象要么由各自的启动日志说明（BackupTargets、Telemetry），
            // 要么会把一行日志撑成几十个元素，反而没人看。
            if (property.PropertyType != typeof(string) && !property.PropertyType.IsValueType) continue;

            var current = property.GetValue(settings);
            var fallback = property.GetValue(defaults);
            if (Equals(current, fallback)) continue;
            changed.Add($"{property.Name}={Format(current)}");
        }

        if (changed.Count == 0)
        {
            logger.LogInformation("配置：全部使用默认值");
            return;
        }

        // 一条日志列出全部差异：分多条会让启动日志刷屏，一条也便于直接复制去比对两台机器。
        logger.LogInformation("非默认配置 {Count} 项：{Settings}", changed.Count, string.Join(", ", changed));
    }

    private static string Format(object? value) => value switch
    {
        null => "(null)",
        string text when text.Length == 0 => "(空)",
        string text => $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "(null)"
    };
}
