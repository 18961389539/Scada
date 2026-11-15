using System.Text.Json;
using Scada.Desktop.Api;
using Scada.Desktop.Configuration;
using Scada.Desktop.Runtime;

namespace Scada.Desktop.Tests;

public sealed class InjectionScriptTests
{
    [Fact]
    public void WritesSessionAndBootMarker()
    {
        var settings = Settings("screen", screenId: 1);
        var session = new SessionData("token-1", "2026-01-01T00:00:00+08:00", """{"id":1,"userName":"kiosk"}""", "zh-CN", false);

        var script = InjectionScript.Build(settings, session, "boot-1");

        Assert.Contains("window.top!==window.self", script);
        Assert.Contains(JsonSerializer.Serialize("http://localhost:5080"), script);
        Assert.Contains("sessionStorage.getItem", script);
        Assert.Contains(JsonSerializer.Serialize("boot-1"), script);
        Assert.Contains("localStorage.setItem", script);
        Assert.Contains(JsonSerializer.Serialize("token-1"), script);
        Assert.Contains(JsonSerializer.Serialize("2026-01-01T00:00:00+08:00"), script);
        Assert.Contains(JsonSerializer.Serialize("""{"id":1,"userName":"kiosk"}"""), script);
        Assert.Contains(JsonSerializer.Serialize("zh-CN"), script);
    }

    [Fact]
    public void ForceScriptSkipsBootGuardButStillSetsMarker()
    {
        var script = InjectionScript.Build(Settings("screen", screenId: 1), Session(), "boot-1", force: true);
        Assert.DoesNotContain("getItem", script);
        Assert.Contains("sessionStorage.setItem", script);
    }

    [Fact]
    public void WritesWorkstationBindingAndEscapesName()
    {
        var settings = Settings("workstation", workstationId: 3);
        settings.Target.WorkstationName = "A\"B\\C\nD";
        var script = InjectionScript.Build(settings, Session(), "boot-1");

        Assert.Contains(JsonSerializer.Serialize("scada_workstation_id"), script);
        Assert.Contains(JsonSerializer.Serialize("3"), script);
        Assert.Contains(JsonSerializer.Serialize("scada_workstation"), script);
        Assert.Contains(JsonSerializer.Serialize("A\"B\\C\nD"), script);
    }

    [Fact]
    public void ClearsStaleWorkstationBindingForOtherTargets()
    {
        var script = InjectionScript.Build(Settings("screen", screenId: 1), Session(), "boot-1");
        Assert.Contains("localStorage.removeItem(" + JsonSerializer.Serialize("scada_workstation_id") + ")", script);
        Assert.Contains("localStorage.removeItem(" + JsonSerializer.Serialize("scada_workstation") + ")", script);
    }

    private static DesktopSettings Settings(string kind, int? screenId = null, int? workstationId = null) => new()
    {
        ServerUrl = "http://localhost:5080",
        Target = new DesktopTarget { Kind = kind, ScreenId = screenId, WorkstationId = workstationId }
    };

    private static SessionData Session() =>
        new("token", "2026-01-01T00:00:00+08:00", """{"id":1}""", null, false);
}
