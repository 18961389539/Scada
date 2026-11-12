using Microsoft.Extensions.Options;
using Scada.Core.Net;
using Scada.Core.Options;

namespace Scada.Server.External;

/// <summary>
/// 对外集成配置的启动期校验：白名单笔误或拼错的安全策略在启动时失败，
/// 而不是等第一次连接被静默拒绝后才排查。
/// </summary>
internal sealed class ExternalIntegrationOptionsValidator : IValidateOptions<ExternalIntegrationOptions>
{
    private static readonly string[] KnownModes = ["none", "sign", "signandencrypt"];

    public ValidateOptionsResult Validate(string? name, ExternalIntegrationOptions options)
    {
        var errors = new List<string>();
        Check("External:Tcp:IpAllowlist", options.Tcp.IpAllowlist, errors);
        Check("External:ModbusSlave:IpAllowlist", options.ModbusSlave.IpAllowlist, errors);
        Check("External:Iec104Server:IpAllowlist", options.Iec104Server.IpAllowlist, errors);

        var modes = options.OpcUa.SecurityModes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant())
            .ToArray();
        if (modes.Length == 0)
        {
            errors.Add("External:OpcUa:SecurityModes 至少需要一个取值（None / Sign / SignAndEncrypt）。");
        }
        else
        {
            foreach (var mode in modes.Where(m => !KnownModes.Contains(m)))
                errors.Add($"External:OpcUa:SecurityModes 含无法识别的安全策略：{mode}。");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Check(string key, string[] rules, List<string> errors)
    {
        foreach (var rule in rules)
        {
            if (!IpRule.IsValid(rule))
                errors.Add($"{key} 含无效规则：{rule ?? "null"}（须为 *、单个地址或 CIDR）。");
        }
    }
}
