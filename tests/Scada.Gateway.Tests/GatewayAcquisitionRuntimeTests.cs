using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Gateway.Hosting;

namespace Scada.Gateway.Tests;

public class GatewayAcquisitionRuntimeTests
{
    private static readonly string Revision = new('A', 64);

    private static GatewayAcquisitionRuntime Runtime(GatewayAgentStoreTests.StoreDirectory temp, int gatewayId = 7) =>
        new(new GatewayAgentOptions { GatewayId = gatewayId }, temp.Open(), new EphemeralDataProtectionProvider());

    private static GatewayConfiguration Configuration(
        string revision = "", IReadOnlyList<GatewayDeviceConfiguration>? devices = null, IReadOnlyList<Tag>? tags = null) =>
        new(revision.Length == 0 ? Revision : revision, devices ?? [], tags ?? []);

    [Fact]
    public void AStoppedRuntimeReportsEmptyStateAndIgnoresStop()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp);

        Assert.Null(runtime.Configuration);
        Assert.Empty(runtime.Statuses);
        Assert.False(runtime.HasFaultedWorker);
        runtime.RetryFaultedWorkers();
        Assert.True(runtime.StopAsync().IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WriteAsyncWithoutAnAppliedConfigurationIsRejectedWithoutTouchingTheDevice()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp);
        var command = new GatewayCommand(Guid.NewGuid(), 1, 2, 1, Revision, 0, long.MaxValue);

        var result = await runtime.WriteAsync(command, CancellationToken.None);

        Assert.False(result.Outcome == GatewayCommandOutcome.Confirmed);
        Assert.Equal(GatewayCommandOutcome.NotSent, result.Outcome);
        Assert.Contains("不一致", result.Message);
    }

    [Fact]
    public void ValidateAcceptsAnEmptyConfigurationWithAWellFormedRevision()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp);

        runtime.Validate(Configuration());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("ZZZ")]
    public void ValidateRejectsMalformedRevisionStrings(string revision)
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp);

        Assert.Throws<InvalidDataException>(() => runtime.Validate(Configuration(revision)));
    }

    [Fact]
    public void ValidateRejectsDuplicateDeviceIds()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp);
        var device = new GatewayDeviceConfiguration(new Device { Id = 1, GatewayId = 7, Name = "Pump" }, null);
        var configuration = Configuration(devices: [device, device]);

        Assert.Throws<InvalidDataException>(() => runtime.Validate(configuration));
    }

    [Fact]
    public void ValidateRejectsDuplicateTagIds()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp);
        var tag = new Tag { Id = 11, DeviceId = 1, Kind = TagKind.Physical, Name = "Temperature" };
        var configuration = Configuration(tags: [tag, tag]);

        Assert.Throws<InvalidDataException>(() => runtime.Validate(configuration));
    }

    [Fact]
    public void ValidateRejectsNonPhysicalTags()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp);
        var device = new Device { Id = 1, GatewayId = 7, Name = "Pump" };
        var tag = new Tag { Id = 11, DeviceId = 1, Kind = TagKind.Calculated, Name = "Calculated" };

        Assert.Throws<InvalidDataException>(() => runtime.Validate(Configuration(devices: [new GatewayDeviceConfiguration(device, null)], tags: [tag])));
    }

    [Fact]
    public void ValidateRejectsDevicesBelongingToAnotherGateway()
    {
        using var temp = new GatewayAgentStoreTests.StoreDirectory();
        var runtime = Runtime(temp, gatewayId: 7);
        var device = new Device { Id = 1, GatewayId = 8, Name = "Pump" };

        Assert.Throws<InvalidDataException>(() => runtime.Validate(Configuration(devices: [new GatewayDeviceConfiguration(device, null)])));
    }
}
