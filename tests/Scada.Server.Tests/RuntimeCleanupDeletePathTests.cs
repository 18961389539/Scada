namespace Scada.Server.Tests;

public sealed class RuntimeCleanupDeletePathTests
{
    [Fact]
    public void DeletePathsInvokeRuntimeCleanupExecutor()
    {
        var root = FindRepoRoot();
        string[] files =
        [
            Path.Combine("src", "Scada.Server", "Services", "DeviceCommandService.cs"),
            Path.Combine("src", "Scada.Server", "Services", "TagCommandService.cs"),
            Path.Combine("src", "Scada.Server", "Endpoints", "AlarmRuleEndpoints.cs"),
            Path.Combine("src", "Scada.Server", "Endpoints", "OeeEndpoints.cs"),
            Path.Combine("src", "Scada.Server", "Endpoints", "SpcEndpoints.cs"),
            Path.Combine("src", "Scada.Engine", "Calculations", "CalcJobConfiguration.cs"),
            Path.Combine("src", "Scada.Engine", "Calculations", "CalculatedTagConfiguration.cs"),
            Path.Combine("src", "Scada.Engine", "Alarms", "AnnunciatorService.cs"),
        ];
        foreach (var rel in files)
        {
            var text = File.ReadAllText(Path.Combine(root, rel));
            Assert.True(text.Contains("AfterDeletedAsync", StringComparison.Ordinal), rel);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Scada.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found");
    }
}