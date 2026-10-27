using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using NModbus;
using Scada.Core.Entities;

namespace Scada.Collector.Modbus;

// Modbus TCP：帧层（MBAP 事务号、协议标识、长度校验）与响应校验由 NModbus 负责；
// Retries=0 保证写入失败不会被库自动重发（见 ModbusMasterDriver）。
public sealed class ModbusTcpDriver(ILogger<ModbusTcpDriver> logger) : ModbusMasterDriver
{
    protected override IModbusMaster Create(Device device)
    {
        var client = new TcpClient { NoDelay = true };
        try { client.Connect(device.Host, device.Port); }
        catch (Exception ex)
        {
            client.Dispose();
            logger.LogWarning("Modbus TCP 连接失败：{Host}:{Port}", device.Host, device.Port);
            throw new IOException($"Modbus TCP 连接失败 {device.Host}:{device.Port}：{ex.Message}", ex);
        }
        return Factory.CreateMaster(client);
    }
}