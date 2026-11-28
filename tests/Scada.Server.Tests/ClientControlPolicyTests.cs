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
    [InlineData("/api/annunciators/ack-visible")]
    [InlineData("/api/alarm-rules/3/shelve")]
    [InlineData("/api/alarm-rules/3/unshelve")]
    [InlineData("/api/alarm-rules/3/out-of-service")]
    [InlineData("/api/calc-jobs/8/acknowledge-writes")]
    [InlineData("/api/traceability/jobs/7/scan")]
    [InlineData("/api/simulation/4/force")]
    [InlineData("/api/simulation/4/release")]
    [InlineData("/api/simulation/4/fault")]
    [InlineData("/api/simulation/4/reset")]
    [InlineData("/api/calc-jobs/8/invoke")]
    [InlineData("/api/calc-jobs/8/reset")]
    [InlineData("/api/calc-jobs/8/maintenance-done")]
    [InlineData("/api/devices/12/diagnostics/reconnect")]
    [InlineData("/api/devices/2/test")]
    [InlineData("/api/devices/test-draft")]
    [InlineData("/api/alarms/9/ack")]
    [InlineData("/api/alarms/9/notes")]
    [InlineData("/api/oee/downtimes")]
    [InlineData("/api/oee/downtimes/4")]
    [InlineData("/api/oee/downtimes/4/confirm")]
    [InlineData("/api/oee/corrections")]
    [InlineData("/api/quality/inspections")]
    [InlineData("/api/quality/inspections/9")]
    [InlineData("/api/quality/inspections/9/delete")]
    [InlineData("/api/work-orders")]
    [InlineData("/api/work-orders/4")]
    [InlineData("/api/work-orders/4/status")]
    [InlineData("/api/work-orders/4/parts")]
    public void ControlPath_Includes_Field_Changing_Routes(string path)
    {
        Assert.Matches(ClientControlPolicy.ControlPath(), path);
    }

    [Theory]
    [InlineData("/api/control/sequences/3/check-start")]
    [InlineData("/api/control/lockouts/4")]
    [InlineData("/api/annunciators")]
    [InlineData("/api/annunciators/2")]
    [InlineData("/api/devices/2/test-draft")]
    [InlineData("/api/devices/2/diagnostics")]
    [InlineData("/api/recipes/5")]
    [InlineData("/api/control/sequences")]
    [InlineData("/api/simulation/4")]
    [InlineData("/api/calc-jobs/8/publish")]
    [InlineData("/api/control/requests/11111111-1111-1111-1111-111111111111")]
    [InlineData("/api/alarms/9")]
    [InlineData("/api/oee/downtimes/import-alarms")]
    [InlineData("/api/oee/analytics")]
    [InlineData("/api/quality/defect-codes")]
    [InlineData("/api/quality/inspections/templates")]
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
            ("Endpoints/AnnunciatorEndpoints.cs", "/api/annunciators/ack-visible"),
            ("Endpoints/AlarmRuleEndpoints.cs", "/api/alarm-rules/1/shelve"),
            ("Endpoints/AlarmRuleEndpoints.cs", "/api/alarm-rules/1/unshelve"),
            ("Endpoints/AlarmRuleEndpoints.cs", "/api/alarm-rules/1/out-of-service"),
            ("Endpoints/TraceabilityEndpoints.cs", "/api/traceability/jobs/1/scan"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/force"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/release"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/fault"),
            ("Endpoints/SimulationEndpoints.cs", "/api/simulation/1/reset"),
            ("Endpoints/CalcJobEndpoints.cs", "/api/calc-jobs/1/invoke"),
            ("Endpoints/CalcJobEndpoints.cs", "/api/calc-jobs/1/reset"),
            ("Endpoints/CalcJobEndpoints.cs", "/api/calc-jobs/1/maintenance-done"),
            ("Endpoints/CalcJobEndpoints.cs", "/api/calc-jobs/1/acknowledge-writes"),
            ("Endpoints/CollectorDiagnosticEndpoints.cs", "/api/devices/1/diagnostics/reconnect"),
            ("Endpoints/DeviceEndpoints.cs", "/api/devices/1/test"),
            ("Endpoints/DeviceEndpoints.cs", "/api/devices/test-draft"),
            ("Endpoints/AlarmEndpoints.cs", "/api/alarms/1/ack"),
            ("Endpoints/AlarmEndpoints.cs", "/api/alarms/1/notes"),
            ("Endpoints/OeeEndpoints.cs", "/api/oee/downtimes"),
            ("Endpoints/OeeEndpoints.cs", "/api/oee/downtimes/1"),
            ("Endpoints/OeeEndpoints.cs", "/api/oee/downtimes/1/confirm"),
            ("Endpoints/OeeEndpoints.cs", "/api/oee/corrections"),
            ("Endpoints/QualityEndpoints.cs", "/api/quality/inspections"),
            ("Endpoints/QualityEndpoints.cs", "/api/quality/inspections/1"),
            ("Endpoints/QualityEndpoints.cs", "/api/quality/inspections/1/delete"),
            ("Endpoints/AssetEndpoints.cs", "/api/work-orders"),
            ("Endpoints/AssetEndpoints.cs", "/api/work-orders/1"),
            ("Endpoints/AssetEndpoints.cs", "/api/work-orders/1/status"),
            ("Endpoints/AssetEndpoints.cs", "/api/work-orders/1/parts"),
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
    [InlineData("POST", "/api/calc-jobs/8/acknowledge-writes", true)]
    [InlineData("GET", "/api/calc-jobs/8/acknowledge-writes", false)]
    [InlineData("POST", "/api/annunciators/ack-visible", true)]
    [InlineData("POST", "/api/alarm-rules/3/shelve", true)]
    [InlineData("POST", "/api/alarm-rules/3/unshelve", true)]
    [InlineData("POST", "/api/alarm-rules/3/out-of-service", true)]
    [InlineData("GET", "/api/calc-jobs/8/reset", false)]
    [InlineData("POST", "/api/devices/12/diagnostics/reconnect", true)]
    [InlineData("GET", "/api/devices/12/diagnostics/reconnect", false)]
    [InlineData("POST", "/api/devices/12/test", true)]
    [InlineData("GET", "/api/devices/12/test", false)]
    [InlineData("POST", "/api/devices/test-draft", true)]
    [InlineData("GET", "/api/devices/test-draft", false)]
    [InlineData("POST", "/api/alarms/9/ack", true)]
    [InlineData("POST", "/api/alarms/9/notes", true)]
    [InlineData("GET", "/api/alarms/9/ack", false)]
    [InlineData("POST", "/api/oee/downtimes", true)]
    [InlineData("PUT", "/api/oee/downtimes/4", true)]
    [InlineData("DELETE", "/api/oee/downtimes/4", true)]
    [InlineData("POST", "/api/oee/downtimes/4/confirm", true)]
    [InlineData("POST", "/api/oee/corrections", true)]
    [InlineData("GET", "/api/oee/downtimes", false)]
    [InlineData("POST", "/api/oee/downtimes/import-alarms", false)]
    [InlineData("POST", "/api/quality/inspections", true)]
    [InlineData("PUT", "/api/quality/inspections/9", true)]
    [InlineData("DELETE", "/api/quality/inspections/9", true)]
    [InlineData("POST", "/api/quality/inspections/9/delete", true)]
    [InlineData("GET", "/api/quality/inspections", false)]
    [InlineData("POST", "/api/work-orders", true)]
    [InlineData("PUT", "/api/work-orders/4", true)]
    [InlineData("POST", "/api/work-orders/4/status", true)]
    [InlineData("POST", "/api/work-orders/4/parts", true)]
    [InlineData("GET", "/api/work-orders", false)]
    public void IsControlRequest_IncludesDeleteFaultAndCalcReset(string method, string path, bool expected)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        Assert.Equal(expected, ClientControlPolicy.IsControlRequest(http));
    }

    [Fact]
    public void IsMonitorOnly_ReadsHeader()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        Assert.False(ClientControlPolicy.IsMonitorOnly(http));
        http.Request.Headers[ClientControlPolicy.MonitorOnlyHeader] = "1";
        Assert.True(ClientControlPolicy.IsMonitorOnly(http));
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? "."); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Scada.sln"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }
}
