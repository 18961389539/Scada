using Microsoft.Extensions.Options;
using Scada.Core.Scripting;

namespace Scada.Engine.Calculations;

/// <summary>
/// C# 脚本配置的可用性规则。以前这些检查只在 <see cref="CSharpScriptHost"/> 构造函数里，
/// 而它是懒加载单例：配错了要等到第一次有人用脚本才炸，日志里看到的是一条 500。
/// 现在同一份规则也挂到 <c>ValidateOnStart</c>，配错就在启动时失败。
/// </summary>
public static class CSharpScriptOptionsValidation
{
    public static IReadOnlyList<string> Validate(CSharpScriptOptions options)
    {
        var errors = new List<string>();
        // 关闭时不校验：历史配置里残留的串口档不该阻断服务启动。
        if (!options.Enabled) return errors;
        foreach (var (name, profile) in options.SerialProfiles ?? new Dictionary<string, SerialProfile>())
            if (SerialProfileRules.Validate(name, profile) is { } error) errors.Add(error);
        if (options.SerialProfiles is { Count: > 0 } && options.TimeoutMs <= options.SerialProfiles.Values.Max(profile => profile.ReadTimeoutMs) + 250)
            errors.Add("Scada:CSharpScripts:TimeoutMs must exceed every serial profile ReadTimeoutMs by at least 250 ms.");
        return errors;
    }
}

public sealed class CSharpScriptOptionsValidator : IValidateOptions<CSharpScriptOptions>
{
    public ValidateOptionsResult Validate(string? name, CSharpScriptOptions options)
    {
        var errors = CSharpScriptOptionsValidation.Validate(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
