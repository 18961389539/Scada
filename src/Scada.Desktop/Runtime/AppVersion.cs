using System.Reflection;

namespace Scada.Desktop.Runtime;

internal static class AppVersion
{
    public static string? Current { get; } = Read();

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var suffix = text.IndexOfAny(['+', '-']);
        if (suffix >= 0) text = text[..suffix];
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? Read()
    {
        try
        {
            var assembly = typeof(AppVersion).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Normalize(!string.IsNullOrWhiteSpace(informational)
                ? informational
                : assembly.GetName().Version?.ToString());
        }
        catch
        {
            return null;
        }
    }
}
