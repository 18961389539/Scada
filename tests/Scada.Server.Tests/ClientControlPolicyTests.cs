using System.Collections.Generic;
using System.IO;
using Scada.Server.Auth;
using Xunit;

namespace Scada.Server.Tests;

public sealed class ClientControlPolicyTests
{
    [Theory]
    [InlineData("/api/commands")]
    [InlineData("/api/screens/12/actions/start")]
    [InlineData("/api/control/requests/11111111-1111-1111-1111-111111111111/approve")]
    [InlineData("/api/control/requests/11111111-1111-1111-1111-111111111111/reject")]
    [InlineData("/api/control/sequences/3/start")]
    [InlineData("/api/control/runs/9/abort")]
    [InlineData("/api/control/lockouts")]
    [InlineData("/api/control/lockouts/4/remove")]
    [InlineData("/api/control/modes/8")]
    [InlineData("/api/recipes/5/download")]
    [InlineData("/api/recipes/downloads/6/abort")]
    [InlineData("/api/annunciators/2/silence")]
    [InlineData("/api/annunciators/2/clear-silence")]
    [InlineData("/api/annunciators/2/test")]
    [InlineData("/api/annunciators/silence-visible")]
    [InlineData("/api/traceability/jobs/7/scan")]
    [InlineData("/api/simulation/4/force")]
    [InlineData("/api/simulation/4/release")]
    [InlineData("/api/simulation/4/fault")]
    [InlineData("/api/simulation/4/reset")]
    [InlineData("/api/calc-jobs/8/invoke")]
    [InlineData("/api/calc-jobs/8/reset")]
    [InlineData("/api/calc-jobs/8/maintenance-done")]
    [InlineData("/api/devices/12/diagnostics/reconnect")]
    public void ControlPath_Includes_Field_Changing_Routes(string path)
    {
        Assert.Matches(ClientControlPolicy.ControlPath(), path);
    }

    [Theory]
    [InlineData("/api/control/sequences/3/check-start")]
    [InlineData("/api/control/lockouts/4")]
    [InlineData("/api/annunciators")]
    [InlineData("/api/annunciators/2")]
    [InlineData("/api/devices/2/test")]
    [InlineData("/api/devices/2/test-draft")]
    [InlineData("/api/devices/test-draft")]
    [InlineData("/api/devices/2/diagnostics")]
    [InlineData("/api/recipes/5")]
    [InlineData("/api/control/sequences")]
    [InlineData("/api/simulation/4")]
    [InlineData("/api/calc-jobs/8/publish")]
    [InlineData("/api/calc-jobs/8/acknowledge-writes")]
    [InlineData("/api/control/requests/11111111-1111-1111-1111-111111111111")]
    public void ControlPath_Ignores_Configuration_Or_Read_Routes(string path)
    {
        Assert.DoesNotMatch(ClientControlPolicy.ControlPath(), path);
    }

    [Fact]
    public void FieldChangingEndpointMaps_AreCoveredByControlPath()
    {
        var root = FindRoot();
        var required = new (string File, string Sample)[]
        {
            ("Endpoints/CommandEndpoints.cs", "/api/commands"),
            ("Endpoints/ScreenEndpoints.cs", "/api/screens/1/actions/node"),
            ("Endpoints/ControlEndpoints.cs", "/api/control/requests/11111111-1111-1111-1111-111111111111/approve"),
            ("Endpoints/ControlEndpoints.cs", "/api/control/requests/11111111-1111-1111-1111-111111111111/reject"),
            ("Endpoints/ControlEndpoints.cs", "/api/control/sequences/1/start"),
            ("Endpoints/ControlEndpoints.cs", "/api/control/runs/1/abort"),
            ("Endpoints/ControlEndpoints.cs", "/api/control/lockouts"),
            ("Endpoints/ControlEndpoints.cs", "/api/control/lockouts/1/remove"),
            ("Endpoints/ControlEndpoints.cs", "/api/control/modes/1"),
            ("Endpoints/RecipeEndpoints.cs", "/api/recipes/1/download"),
            ("Endpoints/RecipeEndpoints.cs", "/api/recipes/downloads/1/abort"),
            ("Endpoints/AnnunciatorEndpoints.cs", "/api/annunciators/1/silence"),
            ("Endpoints/AnnunciatorEndpoints.cs", "/api/annunciators/silence-visible"),
            ("Endpoints/TraceabilityEndpoints.cs", "/api/traceability/jobs/1/scan"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/force"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/release"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/fault"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/reset"),
            ("Endpoints/CalcJobEndpoints.cs", "/api/calc-jobs/1/invoke"),
            ("Endpoints/CalcJobEndpoints.cs", "/api/calc-jobs/1/reset"),
            ("Endpoints/CalcJobEndpoints.cs", "/api/calc-jobs/1/maintenance-done"),
            ("Endpoints/CollectorDiagnosticEndpoints.cs", "/api/devices/1/diagnostics/reconnect"),
        };
        var missing = new List<string>();
        foreach (var (file, sample) in required)
        {
            var path = Path.Combine(root, "src", "Scada.Server", file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing source {file}");
            if (!ClientControlPolicy.ControlPath().IsMatch(sample))
                missing.Add($"{sample} (declared in {file})");
        }
        Assert.True(missing.Count == 0,
            "以下会改变现场或运行值的路径未纳入 ClientControlPolicy.ControlPath，只监视工作站或移动端禁写会被绕过：\n" + string.Join("\n", missing));
    }

    [Theory]
    [InlineData("DELETE", "/api/simulation/4/fault", true)]
    [InlineData("POST", "/api/simulation/4/fault", true)]
    [InlineData("GET", "/api/simulation/4/fault", false)]
    [InlineData("POST", "/api/calc-jobs/8/reset", true)]
    [InlineData("POST", "/api/calc-jobs/8/maintenance-done", true)]
    [InlineData("GET", "/api/calc-jobs/8/reset", false)]
    [InlineData("POST", "/api/devices/12/diagnostics/reconnect", true)]
    [InlineData("GET", "/api/devices/12/diagnostics/reconnect", false)]
    [InlineData("POST", "/api/devices/12/test", false)]
    [InlineData("POST", "/api/devices/test-draft", false)]
    public void IsControlRequest_IncludesDeleteFaultAndCalcReset(string method, string path, bool expected)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        Assert.Equal(expected, ClientControlPolicy.IsControlRequest(http));
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? "."); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Scada.sln"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
