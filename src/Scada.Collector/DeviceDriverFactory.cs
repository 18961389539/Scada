using Microsoft.Extensions.Logging;
using Scada.Collector.Modbus;
using Scada.Collector.S7;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;

namespace Scada.Collector;

public sealed class DeviceDriverFactory(ILoggerFactory loggers, SerialBusPool serialBuses, Opc.OpcUaRuntime opc, IDeviceCredentials credentials) : IDeviceDriverFactory
{
    public IDeviceDriver Create(Device device) => device.GatewayId != null
        ? throw new InvalidOperationException("远程设备必须由指定网关执行采集") : device.Protocol switch
    {
        ProtocolType.ModbusTcp => new ModbusTcpDriver(loggers.CreateLogger<ModbusTcpDriver>()),
        ProtocolType.ModbusRtu => new ModbusRtuDriver(serialBuses),
        ProtocolType.SiemensS7 => new SiemensS7Driver(),
        ProtocolType.OpcUa => new Opc.OpcUaDriver(opc),
        ProtocolType.Mqtt => new Mqtt.MqttDriver(credentials),
        ProtocolType.SparkplugB => new Mqtt.SparkplugDriver(credentials),
        ProtocolType.MitsubishiMc => new Mitsubishi.MitsubishiMcDriver(),
        ProtocolType.OmronFins => new Omron.OmronFinsDriver(),
        ProtocolType.Iec104 => new Iec104.Iec104Driver(),
        ProtocolType.BacnetIp => new Bacnet.BacnetDriver(),
        _ => throw new InvalidOperationException($"不支持设备协议 {device.Protocol}")
    };
}
