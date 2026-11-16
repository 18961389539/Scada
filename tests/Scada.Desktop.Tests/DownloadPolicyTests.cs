using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class DownloadPolicyTests
{
    [Fact]
    public void StripsPathAndInvalidCharacters()
    {
        Assert.Equal("evil.txt", DownloadPolicy.Sanitize(@"..\..\evil.txt"));
        Assert.Equal("a_b_c.txt", DownloadPolicy.Sanitize("a<b>c.txt"));
        Assert.Equal("file", DownloadPolicy.Sanitize("file...  "));
        Assert.Equal("_CON.txt", DownloadPolicy.Sanitize("CON.txt"));
        Assert.Equal("_LPT1", DownloadPolicy.Sanitize("LPT1"));
    }

    [Fact]
    public void FallsBackForEmptyName()
    {
        Assert.Equal("download", DownloadPolicy.Sanitize(""));
        Assert.Equal("download", DownloadPolicy.Sanitize("   "));
    }

    [Fact]
    public void LimitsLengthButKeepsExtension()
    {
        var name = new string('a', 260) + ".txt";
        var sanitized = DownloadPolicy.Sanitize(name);
        Assert.True(sanitized.Length <= 200);
        Assert.EndsWith(".txt", sanitized);
    }

    [Fact]
    public void AddsSequenceForExistingFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scada-downloads");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(directory, "report.csv")
        };
        var target = DownloadPolicy.ResolveDestination(directory, "report.csv", existing.Contains);
        Assert.Equal(Path.Combine(directory, "report (2).csv"), target);
    }

    [Fact]
    public void UsesConfiguredDirectory()
    {
        var target = DownloadPolicy.ResolveDestination(@"D:\Exports", "a.pdf", _ => false);
        Assert.Equal(Path.Combine(@"D:\Exports", "a.pdf"), target);
    }
}
