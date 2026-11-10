using System.Globalization;
using System.Security.Cryptography;
using Scada.Core.Enums;
using Scada.Core.Options;

namespace Scada.Collector.Drivers;

/// <summary>
/// 插件加载策略：默认拒绝一切外部 DLL；只有 SHA-256 在允许列表中的文件才会进入加载。
/// 覆盖内置协议还须显式声明协议名。
/// </summary>
internal sealed class DriverPluginPolicy
{
    private readonly HashSet<string> _allowedSha256;
    private readonly HashSet<ProtocolType> _overrideProtocols;
    private readonly IReadOnlySet<ProtocolType> _builtIns;

    private DriverPluginPolicy(HashSet<string> allowedSha256, HashSet<ProtocolType> overrideProtocols, IReadOnlySet<ProtocolType> builtIns)
    {
        _allowedSha256 = allowedSha256;
        _overrideProtocols = overrideProtocols;
        _builtIns = builtIns;
    }

    public bool HasAllowlist => _allowedSha256.Count > 0;

    public static DriverPluginPolicy From(ScadaSettings settings, IReadOnlySet<ProtocolType> builtIns)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in settings.DriverPluginAllowSha256 ?? [])
        {
            var normalized = NormalizeSha256(raw);
            if (normalized.Length == 64) hashes.Add(normalized);
        }

        var overrides = new HashSet<ProtocolType>();
        foreach (var raw in settings.DriverPluginOverrideProtocols ?? [])
        {
            if (Enum.TryParse<ProtocolType>(raw.Trim(), ignoreCase: true, out var protocol))
                overrides.Add(protocol);
        }

        return new DriverPluginPolicy(hashes, overrides, builtIns);
    }

    public static string FileSha256(string path) =>
        NormalizeSha256(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public static string NormalizeSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        var chars = new char[text.Length];
        var n = 0;
        foreach (var c in text)
        {
            if (c is '-' or ' ' or ':') continue;
            chars[n++] = char.ToUpper(c, CultureInfo.InvariantCulture);
        }
        return new string(chars, 0, n);
    }

    public bool AllowsFile(string sha256, out string? reason)
    {
        if (_allowedSha256.Count == 0)
        {
            reason = "未配置 Scada:DriverPluginAllowSha256，默认拒绝加载未允许的插件";
            return false;
        }
        if (!_allowedSha256.Contains(sha256))
        {
            reason = $"程序集哈希 {sha256} 不在 DriverPluginAllowSha256 允许列表中";
            return false;
        }
        reason = null;
        return true;
    }

    public bool AllowsProtocol(ProtocolType protocol, out string? reason)
    {
        if (_builtIns.Contains(protocol) && !_overrideProtocols.Contains(protocol))
        {
            reason = $"覆盖内置协议 {protocol} 须在 Scada:DriverPluginOverrideProtocols 中显式声明";
            return false;
        }
        reason = null;
        return true;
    }
}
