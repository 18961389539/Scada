namespace Scada.Desktop.Runtime;

internal static class DownloadPolicy
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public static string ResolveDestination(string? directory, string? suggestedFileName, Func<string, bool>? exists = null)
    {
        var root = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory.Trim();
        var name = Sanitize(suggestedFileName);
        var candidate = Path.Combine(root, name);
        var check = exists ?? File.Exists;
        var index = 2;
        while (check(candidate) && index < 10_000)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var extension = Path.GetExtension(name);
            candidate = Path.Combine(root, $"{stem} ({index}){extension}");
            index++;
        }
        return candidate;
    }

    public static string Sanitize(string? suggestedFileName)
    {
        var name = Path.GetFileName(suggestedFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name)) name = "download";
        foreach (var invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        name = new string(name.Where(character => !char.IsControl(character)).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0) name = "download";

        var stem = Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
        if (ReservedNames.Contains(stem)) name = "_" + name;
        if (name.Length > 200)
        {
            var extension = Path.GetExtension(name);
            var keep = Math.Max(1, 200 - extension.Length);
            name = name[..Math.Min(name.Length, keep)] + extension;
        }
        return name;
    }
}
