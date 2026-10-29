using Scada.Collector.Modbus;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Collector.Tests;

// NModbus 的 SerialPortAdapter 不会自动打开串口；驱动必须在 Connect 阶段显式 Open，
// 否则会出现 Connect 成功、首次读取才抛 “The port is closed.” 的假在线状态。
public class ModbusRtuDriverTests
{
    [Fact]
    public async Task ConnectWithoutPortNameFailsBeforeTouchingTheWire()
    {
        var device = new Device { Id = 1, Protocol = ProtocolType.ModbusRtu, SerialPort = "  " };
        await using var driver = new ModbusRtuDriver();

        var error = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(device));

        Assert.Contains("未配置串口名称", error.Message);
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public async Task ConnectReportsOpenFailureForMissingPort()
    {
        var device = new Device { Id = 2, Protocol = ProtocolType.ModbusRtu, SerialPort = "COM9999", BaudRate = 9600 };
        await using var driver = new ModbusRtuDriver();

        var error = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(device));

        Assert.Contains("打开串口 COM9999 失败", error.Message);
        Assert.False(driver.IsConnected);
    }

    [Fact]
    public async Task AsciiDriverUsesTheSameOpenGuard()
    {
        var device = new Device { Id = 3, Protocol = ProtocolType.ModbusAscii, SerialPort = "" };
        await using var driver = new ModbusAsciiDriver();

        var error = await Assert.ThrowsAsync<IOException>(() => driver.ConnectAsync(device));

        Assert.Contains("未配置串口名称", error.Message);
    }
}