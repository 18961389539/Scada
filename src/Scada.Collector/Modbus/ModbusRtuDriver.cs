using System.IO.Ports;
using System.Net.Sockets;
using NModbus;
using NModbus.IO;
using NModbus.Serial;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Collector.Modbus;

// Modbus RTU（RS232/RS485 串口）。
public sealed class ModbusRtuDriver : ModbusMasterDriver
{
    protected override IModbusMaster Create(Device device)
    {
        var port = new SerialPort(device.SerialPort.Trim(), device.BaudRate, (Parity)device.Parity, device.DataBits, (StopBits)device.StopBits)
        { ReadTimeout = Math.Max(100, device.TimeoutMs), WriteTimeout = Math.Max(100, device.TimeoutMs) };
        return Factory.CreateRtuMaster(new SerialPortAdapter(port));
    }
}

// Modbus ASCII（串口，帧以 ':' 起、CRLF 止，LRC 校验）。
public sealed class ModbusAsciiDriver : ModbusMasterDriver
{
    protected override IModbusMaster Create(Device device)
    {
        var port = new SerialPort(device.SerialPort.Trim(), device.BaudRate, (Parity)device.Parity, device.DataBits, (StopBits)device.StopBits)
        { ReadTimeout = Math.Max(100, device.TimeoutMs), WriteTimeout = Math.Max(100, device.TimeoutMs) };
        return Factory.CreateAsciiMaster(new SerialPortAdapter(port));
    }
}

// Modbus RTU over TCP：RTU 帧直接跑在 TCP 上，不带 MBAP 头（常见于串口服务器的透明传输）。
public sealed class ModbusRtuOverTcpDriver : ModbusMasterDriver
{
    protected override IModbusMaster Create(Device device)
    {
        var client = new TcpClient { NoDelay = true };
        try { client.Connect(device.Host, device.Port); }
        catch (Exception ex) { client.Dispose(); throw new IOException($"Modbus RTU over TCP 连接失败 {device.Host}:{device.Port}：{ex.Message}", ex); }
        return Factory.CreateRtuMaster(new TcpClientAdapter(client));
    }
}