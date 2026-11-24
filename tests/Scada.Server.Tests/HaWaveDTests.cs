using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Engine.History;
using Scada.Engine.Maintenance;
using Scada.Server.Ha;

namespace Scada.Server.Tests;

public sealed class HaWaveDTests
{
    [Fact]
    public void StandaloneWindowsUsesDpapiAndHaDoesNot()
    {
        Assert.Equal(OperatingSystem.IsWindows(), HaDataProtection.UseMachineDpapi(haEnabled: false));
        Assert.False(HaDataProtection.UseMachineDpapi(haEnabled: true));
    }

    [Fact]
    public void SecretSyncRoundTripsKeysAndPki()
    {
        var root = Path.Combine(Path.GetTempPath(), "ha-secrets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keys = Path.Combine(root, "keys");
            var pki = Path.Combine(root, "opc-pki");
            Directory.CreateDirectory(keys);
            Directory.CreateDirectory(pki);
            File.WriteAllText(Path.Combine(keys, "key-1.xml"), "<key id=\"1\" />");
            File.WriteAllText(Path.Combine(keys, "report-signing.pfx"), "pfx");
            File.WriteAllText(Path.Combine(pki, "own.pfx"), "pki");
            var snapshot = HaSecretSync.Snapshot(Path.Combine(root, "state"), keys, pki);
            Assert.NotNull(snapshot);
            Assert.False(string.IsNullOrEmpty(snapshot!.Value.Sha));
            var destKeys = Path.Combine(root, "dest-keys");
            var destPki = Path.Combine(root, "dest-pki");
            HaSecretSync.Apply(snapshot.Value.Path, destKeys, destPki);
            Assert.Equal("<key id=\"1\" />", File.ReadAllText(Path.Combine(destKeys, "key-1.xml")));
            Assert.Equal("pfx", File.ReadAllText(Path.Combine(destKeys, "report-signing.pfx")));
            Assert.Equal("pki", File.ReadAllText(Path.Combine(destPki, "own.pfx")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void RuntimeLagWarnsLikeHistoryAndRecordsTables()
    {
        var lag = new HaHistoryLagMonitor("运行库复制");
        lag.Observe(100, ["AlarmEvents"]);
        lag.Observe(100, ["AlarmEvents", "NotificationOutbox"]);
        lag.Observe(120, ["ControlSequenceRuns"]);
        var warning = lag.Warning();
        Assert.NotNull(warning);
        Assert.Contains("运行库复制", warning);
        Assert.Contains("连续 3 轮未能追平", warning);
        Assert.Contains("ControlSequenceRuns", lag.Tables);
    }

    [Fact]
    public void ReportArchivePathsRoundTripRelativeAndLegacyAbsolute()
    {
        var root = Path.Combine(Path.GetTempPath(), "reports-" + Guid.NewGuid().ToString("N"));
        try
        {
            var full = Path.Combine(root, "3", "run.xlsx");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "x");
            var relative = ReportArchivePaths.Relativize(root, full);
            Assert.Equal("3/run.xlsx", relative.Replace('\\', '/'));
            Assert.Equal(Path.GetFullPath(full), ReportArchivePaths.Resolve(root, relative));
            var movedRoot = Path.Combine(Path.GetTempPath(), "reports-new-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(movedRoot, "3"));
            File.Copy(full, Path.Combine(movedRoot, "3", "run.xlsx"));
            var resolved = ReportArchivePaths.Resolve(movedRoot, full);
            Assert.True(File.Exists(resolved));
            Assert.StartsWith(Path.GetFullPath(movedRoot), Path.GetFullPath(resolved), StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
