using Microsoft.Extensions.Logging;
using Scada.Collector.Drivers;
using Scada.Collector.Modbus;
using Scada.Collector.S7;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

namespace Scada.Collector;

/// <summary>
/// 驱动工厂：先查已按策略加载的外部插件，再落到内置实现。
/// 覆盖已有内置协议必须在配置中显式声明；IEC 61850 无内置实现，只需通过允许列表即可提供。
/// </summary>
public sealed class DeviceDriverFactory : IDeviceDriverFactory
{
    /// <summary>产品内置的协议槽位。IEC 61850 不在其中（libiec61850 为 GPLv3，不能随产品分发）。</summary>
    public static IReadOnlySet<ProtocolType> BuiltInProtocols { get; } = new HashSet<ProtocolType>
    {
        ProtocolType.ModbusTcp, ProtocolType.ModbusRtu, ProtocolType.ModbusAscii, ProtocolType.ModbusRtuOverTcp,
        ProtocolType.SiemensS7, ProtocolType.OpcUa, ProtocolType.Mqtt, ProtocolType.SparkplugB,
        ProtocolType.MitsubishiMc, ProtocolType.OmronFins, ProtocolType.Iec104, ProtocolType.BacnetIp,
        ProtocolType.OpcDa, ProtocolType.EtherNetIp
    };

    private readonly ILogger _log;
    private readonly Simulation.SimulationRuntime _simulations;
    private readonly DriverPluginCatalog _plugins;
    private readonly Dictionary<ProtocolType, BuiltInDriver> _builtIns;

    private sealed record BuiltInDriver(string Name, Func<IDeviceDriver> Create);

    public DeviceDriverFactory(ILoggerFactory loggers, Opc.OpcUaRuntime opc, IDeviceCredentials credentials, Simulation.SimulationRuntime simulations, DriverPluginCatalog plugins)
    {
        _log = loggers.CreateLogger<DeviceDriverFactory>();
        _simulations = simulations;
        _plugins = plugins;
        _builtIns = new Dictionary<ProtocolType, BuiltInDriver>
        {
            [ProtocolType.ModbusTcp] = new("ModbusTcp", () => new ModbusTcpDriver(loggers.CreateLogger<ModbusTcpDriver>())),
            [ProtocolType.ModbusRtu] = new("ModbusRtu", () => new ModbusRtuDriver()),
            [ProtocolType.ModbusAscii] = new("ModbusAscii", () => new ModbusAsciiDriver()),
            [ProtocolType.ModbusRtuOverTcp] = new("ModbusRtuOverTcp", () => new ModbusRtuOverTcpDriver()),
            [ProtocolType.SiemensS7] = new("SiemensS7", () => new SiemensS7Driver()),
            [ProtocolType.OpcUa] = new("OpcUa", () => new Opc.OpcUaDriver(opc)),
            [ProtocolType.Mqtt] = new("Mqtt", () => new Mqtt.MqttDriver(credentials)),
            [ProtocolType.SparkplugB] = new("SparkplugB", () => new Mqtt.SparkplugDriver(credentials)),
            [ProtocolType.MitsubishiMc] = new("MitsubishiMc", () => new Mitsubishi.MitsubishiMcDriver()),
            [ProtocolType.OmronFins] = new("OmronFins", () => new Omron.OmronFinsDriver()),
            [ProtocolType.Iec104] = new("Iec104", () => new Iec104.Iec104Driver()),
            [ProtocolType.BacnetIp] = new("BacnetIp", () => new Bacnet.BacnetDriver()),
            [ProtocolType.OpcDa] = new("OpcDa", () => new OpcDa.OpcDaDriver()),
            [ProtocolType.EtherNetIp] = new("EtherNetIp", () => new EtherNetIp.EtherNetIpDriver())
            // IEC 61850 没有内置实现：libiec61850 为 GPLv3/商业双许可，不能随产品分发。
            // 由插件提供；未安装插件时 Iec61850Spec 会阻止设备启用，工厂再兜底报清晰错误。
        };
    }

    public IDeviceDriver Create(Device device)
    {
        // 仿真设备不打开现场连接，也不允许经网关下发；校验层已阻止仿真设备分配到网关。
        if (device.SimulationEnabled)
        {
            if (device.GatewayId != null) throw new InvalidOperationException("仿真设备不能由网关采集，请关闭仿真或改回本地采集");
            return new Simulation.SimulationDeviceDriver(_simulations);
        }
        if (device.GatewayId != null) throw new InvalidOperationException("远程设备必须由指定网关执行采集");

        if (_plugins.TryCreate(device.Protocol, out var driver, out var providerName))
        {
            _log.LogDebug("设备 {Device} 使用插件驱动 {Provider}", device.Name, providerName);
            return driver!;
        }
        if (_builtIns.TryGetValue(device.Protocol, out var builtIn)) return builtIn.Create();
        if (device.Protocol == ProtocolType.Iec61850) throw new NotSupportedException(Iec61850Spec.NotInstalledMessage);
        throw new InvalidOperationException($"不支持设备协议 {device.Protocol}");
    }

    /// <summary>内置驱动与外部插件的完整来源清单，含加载失败原因。</summary>
    public DriverPluginCatalogSnapshot Snapshot() =>
        _plugins.Snapshot(_builtIns.Select(x => (x.Key, x.Value.Name, "built-in")).ToList());
}