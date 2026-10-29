using System.IO.Ports;
using System.Net.Sockets;
using NModbus;
using NModbus.IO;
using NModbus.Serial;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Collector.Modbus;

// 串口类 Modbus 的公共部分。
// NModbus 的 SerialPortAdapter 只包装已打开的 SerialPort，不会替调用方 Open；
// 不显式打开时 Connect 会“假成功”，首次读写才抛 “The port is closed.”。
public abstract class SerialModbusDriver : ModbusMasterDriver
{
    private SerialPort? _port;

    protected abstract IModbusMaster CreateMaster(SerialPort port);

    protected override IModbusMaster Create(Device device)
    {
        var portName = device.SerialPort?.Trim() ?? string.Empty;
        if (portName.Length == 0) throw new IOException("未配置串口名称（SerialPort），请在设备配置中填写 COM 口");
        var port = new SerialPort(portName, device.BaudRate, (Parity)device.Parity, device.DataBits, (StopBits)device.StopBits)
        {
            ReadTimeout = Math.Max(100, device.TimeoutMs),
            WriteTimeout = Math.Max(100, device.TimeoutMs)
        };
        try
        {
            port.Open();
        }
        catch (Exception ex)
        {
            port.Dispose();
            throw new IOException($"打开串口 {portName} 失败：{ex.Message}。请检查 COM 口号、端口是否被其他程序占用、USB 转 RS485 驱动是否正常。", ex);
        }
        _port = port;
        try { return CreateMaster(port); }
        catch
        {
            Interlocked.Exchange(ref _port, null);
            try { port.Dispose(); } catch (Exception) { }
            throw;
        }
    }

    public override void Disconnect()
    {
        // 先让 NModbus 释放主站，再兜底关闭本驱动持有的端口，避免换口/重连后端口泄漏。
        var port = Interlocked.Exchange(ref _port, null);
        try { base.Disconnect(); }
        finally
        {
            try { if (port?.IsOpen == true) port.Close(); } catch (Exception) { }
            try { port?.Dispose(); } catch (Exception) { }
        }
    }
}

// Modbus RTU（RS232/RS485 串口）。
public sealed class ModbusRtuDriver : SerialModbusDriver
{
    protected override IModbusMaster CreateMaster(SerialPort port) => Factory.CreateRtuMaster(new SerialPortAdapter(port));
}

// Modbus ASCII（串口，帧以 ':' 起、CRLF 止，LRC 校验）。
public sealed class ModbusAsciiDriver : SerialModbusDriver
{
    protected override IModbusMaster CreateMaster(SerialPort port) => Factory.CreateAsciiMaster(new SerialPortAdapter(port));
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