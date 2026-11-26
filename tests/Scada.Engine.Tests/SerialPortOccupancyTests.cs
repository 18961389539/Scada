using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;

namespace Scada.Engine.Tests;

public sealed class SerialPortOccupancyTests
{
    [Fact]
    public void ConflictReportsScannerAndIgnoresSameDevice()
    {
        var owners = SerialPortOccupancy.FromScanners([(1, "扫码", "COM3")])
            .Concat(SerialPortOccupancy.FromExclusiveDevices([
                new Device { Id = 7, Name = "PLC", Protocol = ProtocolType.ModbusRtu, SerialPort = "COM3", Enabled = true }
            ]))
            .ToList();
        var hit = SerialPortOccupancy.Conflict(owners, "COM3", ignore: owner => owner.Kind == SerialPortOccupancy.Device);
        Assert.Contains("追溯扫码", hit);
        Assert.Null(SerialPortOccupancy.Conflict(owners, "COM4"));
    }
}
