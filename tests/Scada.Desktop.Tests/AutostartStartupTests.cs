using Scada.Desktop.Configuration;

namespace Scada.Desktop.Tests;

public sealed class AutostartStartupTests
{
    private static DesktopSettings Settings(bool autoStart = true, bool managed = false) =>
        new() { AutoStart = autoStart, ManagedAutostart = managed };

    [Fact]
    public void FailureWarningIsReported()
    {
        var logged = new List<string>();
        AutostartStartup.Apply(Settings(), (_, _) => AutostartResult.Failed("注册表被组策略拦截"), logged.Add);

        Assert.Equal("注册表被组策略拦截", Assert.Single(logged));
    }

    [Fact]
    public void SuccessDoesNotReport()
    {
        var logged = new List<string>();
        AutostartStartup.Apply(Settings(), (_, _) => AutostartResult.Success(), logged.Add);

        Assert.Empty(logged);
    }

    [Fact]
    public void ManagedAutostartIsNotTreatedAsFailure()
    {
        var logged = new List<string>();
        AutostartStartup.Apply(Settings(managed: true), (_, _) => AutostartResult.Managed(), logged.Add);

        Assert.Empty(logged);
    }

    [Fact]
    public void CurrentSettingsAreForwarded()
    {
        bool? enabled = null, managed = null;
        AutostartStartup.Apply(Settings(autoStart: false, managed: true), (e, m) =>
        {
            enabled = e;
            managed = m;
            return AutostartResult.Success();
        }, _ => { });

        Assert.False(enabled);
        Assert.True(managed);
    }
}
