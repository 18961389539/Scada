using System.Globalization;
using System.Text.RegularExpressions;

namespace Scada.Core.Models;

public static partial class OpcNodeAddress
{
    public static bool IsValid(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 2048 || address.Any(char.IsControl)) return false;
        var match = Syntax().Match(address);
        if (!match.Success) return false;
        if (match.Groups["ns"].Success && !ushort.TryParse(match.Groups["ns"].Value, out _)) return false;
        if (match.Groups["uri"].Success && !Uri.TryCreate(match.Groups["uri"].Value, UriKind.Absolute, out _)) return false;
        var value = match.Groups["value"].Value;
        return match.Groups["kind"].Value switch
        {
            "i" => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && (number != 0 || match.Groups["ns"].Value is not ("" or "0") || match.Groups["uri"].Success),
            "g" => Guid.TryParse(value, out _),
            "b" => Convert.TryFromBase64String(value, new byte[value.Length], out _),
            _ => true
        };
    }
    [GeneratedRegex("^(?:(?:ns=(?<ns>[0-9]{1,5})|nsu=(?<uri>[^;]+));)?(?<kind>[isgb])=(?<value>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Syntax();
}
