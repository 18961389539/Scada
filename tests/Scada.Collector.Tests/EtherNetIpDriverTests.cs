using System.IO;
using System.Reflection;
using Scada.Collector.EtherNetIp;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using PlcStatus = libplctag.Status;

namespace Scada.Collector.Tests;

public class EtherNetIpDriverTests
{
    private static Scada.Core.Entities.Device Device() => new()
    {
        Id = 1, Name = "eip", Protocol = ProtocolType.EtherNetIp,
        Host = "127.0.0.1", Port = 1, TimeoutMs = 500
    };

    private static Scada.Core.Entities.Tag Tag(bool writable = false) => new()
    {
        Id = 1, DeviceId = 1, Name = "tag", Area = TagArea.Address,
        NodeId = "MyTag", DataType = TagDataType.Int32, Writable = writable
    };

    private static object? Static(string name, params object[] args) =>
        typeof(EtherNetIpDriver).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);

    [Fact]
    public async Task DisconnectedDriverRejectsReadAndWriteWithoutOpeningAConnection()
    {
        await using var driver = new EtherNetIpDriver();
        var device = Device();
        var plan = new ReadPlan { DeviceId = device.Id, Tags = [Tag()] };

        Assert.False(driver.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => driver.ReadAsync(device, plan));

        var readOnly = Tag();
        await Assert.ThrowsAsync<InvalidOperationException>(() => driver.WriteAsync(device, readOnly, 1));

        var writable = Tag(writable: true);
        var failure = await Assert.ThrowsAsync<IOException>(() => driver.WriteAsync(device, writable, 1));
        Assert.Contains("未连接", failure.Message);
        Assert.Equal(GatewayCommandOutcome.NotSent, CommandFailure.Read(failure));
    }

    [Fact]
    public async Task ConnectFailureLeavesDriverDisconnectedAndRecordsReason()
    {
        await using var driver = new EtherNetIpDriver();
        await Assert.ThrowsAnyAsync<Exception>(() => driver.ConnectAsync(Device()));
        Assert.False(driver.IsConnected);
        Assert.False(string.IsNullOrWhiteSpace(driver.LastError));
        Assert.Null(driver.Identity);
    }

    [Theory]
    [InlineData("ControlLogix", "ControlLogix")]
    [InlineData("CompactLogix", "ControlLogix")]
    [InlineData("Micro800", "Micro800")]
    [InlineData("Slc500", "Slc500")]
    [InlineData("Plc5", "Plc5")]
    [InlineData("LogixPccc", "LogixPccc")]
    [InlineData("MicroLogix", "MicroLogix")]
    [InlineData("Omron", "Omron")]
    public void PlcTypeMappingCoversEveryDocumentedModel(string configured, string expected)
    {
        Assert.Equal(expected, Static("MapPlcType", configured)!.ToString());
    }

    [Fact]
    public void UnknownPlcTypeAndElementSizesAreClassified()
    {
        var unknown = Assert.Throws<TargetInvocationException>(() => Static("MapPlcType", "Unknown"));
        Assert.IsType<InvalidOperationException>(unknown.InnerException);

        Assert.Equal(2, Static("ExpectedSize", TagDataType.Int16));
        Assert.Equal(2, Static("ExpectedSize", TagDataType.UInt16));
        Assert.Equal(4, Static("ExpectedSize", TagDataType.Int32));
        Assert.Equal(4, Static("ExpectedSize", TagDataType.UInt32));
        Assert.Equal(4, Static("ExpectedSize", TagDataType.Float));
        Assert.Equal(8, Static("ExpectedSize", TagDataType.Double));
        Assert.Equal(0, Static("ExpectedSize", TagDataType.Bool));
    }

    [Fact]
    public void StatusFormattingAndDeviceLevelClassificationAreStable()
    {
        Assert.Equal("EtherNet/IP Timeout: boom", Static("Describe", PlcStatus.ErrorTimeout, new IOException("boom")));
        Assert.Equal("EtherNet/IP BadStatus: boom", Static("Describe", PlcStatus.ErrorBadStatus, new IOException("boom")));

        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorBadConnection)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorBadGateway)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorTimeout)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorOpen)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorWinsock)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorNoResources)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorNoMem)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorBadDevice)!);
        Assert.True((bool)Static("IsDeviceLevel", PlcStatus.ErrorThreadCreate)!);
        Assert.False((bool)Static("IsDeviceLevel", PlcStatus.ErrorBadStatus)!);
    }
}