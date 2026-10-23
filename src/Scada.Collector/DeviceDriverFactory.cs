using Microsoft.Extensions.Logging;
using Scada.Collector.Modbus;
using Scada.Collector.S7;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;

namespace Scada.Collector;

public sealed class DeviceDriverFactory(ILoggerFactory loggers, SerialBusPool serialBuses) : IDeviceDriverFactory
{
    public IDeviceDriver Create(Device device) => device.Protocol switch
    {
        ProtocolType.ModbusTcp => new ModbusTcpDriver(loggers.CreateLogger<ModbusTcpDriver>()),
        ProtocolType.ModbusRtu => new ModbusRtuDriver(serialBuses),
        ProtocolType.SiemensS7 => new SiemensS7Driver(),
        _ => throw new InvalidOperationException($"不支持设备协议 {device.Protocol}")
    };
}
