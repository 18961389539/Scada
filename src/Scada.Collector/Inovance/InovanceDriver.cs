using System.IO.Ports;
using System.Net.Sockets;
using NModbus;
using NModbus.IO;
using NModbus.Serial;
using Scada.Collector.Common;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;
using Scada.Protocols;

namespace Scada.Collector.Inovance;

/// <summary>
/// 汇川 H3U / H5U / Easy / AM / AC。测点地址是软元件，读之前换成 Modbus 线圈、离散输入或保持寄存器。
/// </summary>
public sealed class InovanceDriver : IDeviceDriver, ITransactionGatedDriver
{
    private const int MaxRegisters = 125;
    private const int MaxCoils = 2000;
    private static readonly IModbusFactory Factory = new ModbusFactory();
    private readonly SemaphoreSlim _wire = new(1, 1);
    private IModbusMaster? _master;
    private SerialPort? _port;
    private ITransactionGate? _gate;

    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>共享串口时按单次请求排队。未绑定通道时为 null，读写与原来一致。</summary>
    public ITransactionGate? TransactionGate { set => _gate = value; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        await _wire.WaitAsync(cancellationToken);
        try
        {
            Disconnect();
            var options = ProtocolCatalog.Options<InovanceDeviceOptions>(device);
            var master = options.Transport == InovanceTransport.Rtu
                ? await Task.Run(() => OpenRtu(device), cancellationToken)
                : await OpenTcpAsync(device, cancellationToken);
            if (master.Transport is ModbusTransport transport)
            {
                transport.Retries = 0;
                transport.ReadTimeout = Math.Max(100, device.TimeoutMs);
                transport.WriteTimeout = Math.Max(100, device.TimeoutMs);
            }
            _master = master;
            IsConnected = true;
            LastError = null;
        }
        catch (Exception ex) { IsConnected = false; LastError = ex.Message; throw; }
        finally { _wire.Release(); }
    }

    public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<RawTagValue>>(() => ReadCore(device, plan, cancellationToken), cancellationToken);

    private IReadOnlyList<RawTagValue> ReadCore(Device device, ReadPlan plan, CancellationToken cancellationToken)
    {
        var master = _master ?? throw new IOException("汇川 PLC 未连接");
        var options = ProtocolCatalog.Options<InovanceDeviceOptions>(device);
        var result = new List<RawTagValue>();
        var mapped = new List<(Tag Tag, InovancePoint Point)>();
        foreach (var tag in plan.Tags)
        {
            if (InovanceAddress.TryMap(options.Series, tag.NodeId, tag.DataType, tag.Writable, out var point, out var error)) mapped.Add((tag, point));
            else result.Add(new(tag.Id, 0, TagQuality.Bad, null, error));
        }
        try
        {
            foreach (var area in mapped.GroupBy(item => item.Point.Area))
            {
                var max = area.Key is TagArea.Coil or TagArea.DiscreteInput ? MaxCoils : MaxRegisters;
                var spans = SpanMerger.Merge(area.Select(item => (item, item.Point.Address, item.Point.Length)), options.MaxGap, max);
                foreach (var span in spans)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] bytes;
                    try { bytes = Transact(device.Id, () => ReadSpan(master, device, area.Key, span.Start, span.Length), cancellationToken); }
                    catch (Exception ex) when (IsSlaveRejection(ex))
                    {
                        foreach (var (item, _, _) in span.Items)
                            result.Add(new(item.Tag.Id, 0, TagQuality.Bad, null, SoftElementFault.Rejected(item.Tag.NodeId, item.Point.Area, item.Point.Address, item.Point.Length, item.Point.Bit, ModbusSlaveReason.Describe(ex))));
                        continue;
                    }
                    foreach (var (item, offset, _) in span.Items)
                        result.Add(Decode(item.Tag, item.Point, bytes, offset, SoftElementByteOrder.Effective(device)));
                }
            }
            IsConnected = true;
            LastError = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsConnected = false;
            throw new IOException(ex.Message, ex);
        }
    }

    public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var master = _master ?? throw new IOException("汇川 PLC 未连接");
        var options = ProtocolCatalog.Options<InovanceDeviceOptions>(device);
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!InovanceAddress.TryMap(options.Series, tag.NodeId, tag.DataType, writable: true, out var point, out var error))
            throw new InvalidOperationException(error);
        if (point.WordBit) throw new InvalidOperationException("字的位地址只读");
        try
        {
            Transact(device.Id, () =>
            {
                var station = DeviceLink.From(device).Station;
                if (point.Area == TagArea.Coil) master.WriteSingleCoil(station, checked((ushort)point.Address), value != 0);
                else
                {
                    var bytes = RegisterValueCodec.EncodeRegister(value, tag.DataType, SoftElementByteOrder.Effective(device));
                    master.WriteMultipleRegisters(station, checked((ushort)point.Address), ToWords(bytes));
                }
                return 0;
            }, cancellationToken);
            LastError = null;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not ArgumentException and not OperationCanceledException)
        {
            LastError = ex.Message;
            if (!IsSlaveRejection(ex)) IsConnected = false;
            throw;
        }
    }, cancellationToken);

    public void Disconnect()
    {
        var master = Interlocked.Exchange(ref _master, null);
        var port = Interlocked.Exchange(ref _port, null);
        try { master?.Dispose(); } catch { /* 关闭异常忽略：串口可能已被拔出 */ }
        try { if (port?.IsOpen == true) port.Close(); } catch { /* 端口已失效 */ }
        try { port?.Dispose(); } catch { /* 端口已失效 */ }
        IsConnected = false;
    }

    public ValueTask DisposeAsync() { Disconnect(); _wire.Dispose(); return ValueTask.CompletedTask; }

    private static RawTagValue Decode(Tag tag, InovancePoint point, byte[] bytes, int offset, ByteOrder order)
    {
        if (point.Area is TagArea.Coil or TagArea.DiscreteInput)
            return new(tag.Id, RegisterValueCodec.DecodeCoil(bytes, offset) ? 1 : 0);
        if (point.Bit is int bit)
        {
            var word = (ushort)RegisterValueCodec.DecodeRegister(bytes, offset, TagDataType.UInt16, order);
            return new(tag.Id, (word >> bit) & 1);
        }
        var value = RegisterValueCodec.DecodeRegister(bytes, offset, tag.DataType, order);
        return double.IsFinite(value) ? new(tag.Id, value) : new(tag.Id, 0, TagQuality.Bad, null, "PLC 返回非有限浮点值");
    }

    private T Transact<T>(int deviceId, Func<T> operation, CancellationToken cancellationToken) =>
        _gate is null ? operation() : _gate.Execute(deviceId, operation, cancellationToken);

    private static byte[] ReadSpan(IModbusMaster master, Device device, TagArea area, int start, int length)
    {
        var station = DeviceLink.From(device).Station;
        var address = checked((ushort)start);
        var count = checked((ushort)length);
        return area switch
        {
            TagArea.Coil => ToBits(master.ReadCoils(station, address, count)),
            TagArea.DiscreteInput => ToBits(master.ReadInputs(station, address, count)),
            _ => ToRegisters(master.ReadHoldingRegisters(station, address, count))
        };
    }

    private static async Task<IModbusMaster> OpenTcpAsync(Device device, CancellationToken cancellationToken)
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
            throw new IOException($"汇川 Modbus TCP 连接失败 {link.Host}:{link.Port}：{ex.Message}", ex);
        }
    }

    private IModbusMaster OpenRtu(Device device)
    {
        var link = DeviceLink.From(device);
        var portName = link.SerialPort.Trim();
        if (portName.Length == 0) throw new IOException("未配置串口名称");
        var port = new SerialPort(portName, link.BaudRate, (Parity)link.Parity, link.DataBits, (StopBits)link.StopBits)
        {
            ReadTimeout = Math.Max(100, link.TimeoutMs),
            WriteTimeout = Math.Max(100, link.TimeoutMs)
        };
        try { port.Open(); }
        catch (Exception ex)
        {
            port.Dispose();
            throw new IOException($"打开串口 {portName} 失败：{ex.Message}", ex);
        }
        _port = port;
        try { return Factory.CreateRtuMaster(new SerialPortAdapter(port)); }
        catch
        {
            Interlocked.Exchange(ref _port, null);
            try { port.Dispose(); } catch { /* 打开后创建主站失败，端口一并释放 */ }
            throw;
        }
    }

    private static bool IsSlaveRejection(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SlaveException) return true;
        return false;
    }

    private static byte[] ToRegisters(ushort[] words)
    {
        var bytes = new byte[words.Length * 2];
        for (var i = 0; i < words.Length; i++)
        { bytes[i * 2] = (byte)(words[i] >> 8); bytes[i * 2 + 1] = (byte)words[i]; }
        return bytes;
    }

    private static ushort[] ToWords(byte[] bytes)
    {
        var words = new ushort[bytes.Length / 2];
        for (var i = 0; i < words.Length; i++) words[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
        return words;
    }

    private static byte[] ToBits(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++) if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        return bytes;
    }
}
