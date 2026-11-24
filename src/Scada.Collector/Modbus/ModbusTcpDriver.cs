using Microsoft.Extensions.Logging;
using NModbus;
using Scada.Collector.Common;
using Scada.Core.Entities;
using Scada.Core.Protocols;

namespace Scada.Collector.Modbus;

// Modbus TCP：帧层（MBAP 事务号、协议标识、长度校验）与响应校验由 NModbus 负责；
// Retries=0 保证写入失败不会被库自动重发（见 ModbusMasterDriver）。
public sealed class ModbusTcpDriver(ILogger<ModbusTcpDriver> logger) : ModbusMasterDriver
{
    protected override async Task<IModbusMaster> CreateAsync(Device device, CancellationToken cancellationToken)
    {
        var link = DeviceLink.From(device);
        try
        {
            var client = await TcpConnect.ConnectAsync(link.Host, link.Port, device.TimeoutMs, cancellationToken);
            return Factory.CreateMaster(client);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("Modbus TCP 连接失败：{Host}:{Port}", link.Host, link.Port);
            throw new IOException($"Modbus TCP 连接失败 {link.Host}:{link.Port}：{ex.Message}", ex);
        }
    }
}