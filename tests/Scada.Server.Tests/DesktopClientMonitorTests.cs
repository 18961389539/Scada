using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Server.Desktop;

namespace Scada.Server.Tests;

public sealed class DesktopClientMonitorTests
{
    [Fact]
    public void HealthWarningsAppearWhenRegisteredClientGoesOfflineAndClearOnRecovery()
    {
        var clock = new MutableTimeProvider();
        var registry = new DesktopClientRegistry(NullLogger<DesktopClientRegistry>.Instance, clock);
        var settings = new ScadaSettings { DesktopClient = new DesktopClientOptions { OnlineThresholdSeconds = 90 } };
        var monitor = new DesktopClientMonitor(registry, new StaticOptions(settings), NullLogger<DesktopClientMonitor>.Instance);
        var clientId = Guid.NewGuid().ToString("D");

        registry.Heartbeat(new DesktopClientHeartbeat(clientId, "操作台-1", "operator", "1.0.0", "131.0", "boot", "screen", 1, "总览", "user", null),
            1, "kiosk", "127.0.0.1", "A", settings.DesktopClient);
        Assert.Empty(monitor.HealthWarnings());

        clock.Now = clock.Now.AddSeconds(91);
        registry.Sweep(settings.DesktopClient);
        var warning = Assert.Single(monitor.HealthWarnings());
        Assert.Equal(DesktopClientMonitor.WarningCode, warning.Code);
        Assert.Contains("操作台-1", warning.Message);
        Assert.Contains(clientId, warning.Message);

        registry.Heartbeat(new DesktopClientHeartbeat(clientId, "操作台-1", "operator", "1.0.0", "131.0", "boot", "screen", 1, "总览", "user", null),
            1, "kiosk", "127.0.0.1", "A", settings.DesktopClient);
        Assert.Empty(monitor.HealthWarnings());
    }

    private sealed class StaticOptions(ScadaSettings value) : IOptionsMonitor<ScadaSettings>
    {
        public ScadaSettings CurrentValue { get; } = value;
        public ScadaSettings Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ScadaSettings, string?> listener) => null;
    }
}
