using NModbus;
using NModbus.IO;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Modbus;

// 基于 NModbus 的 Modbus 驱动基类。
// 帧层（RTU CRC / ASCII LRC / TCP MBAP）、从站异常码解析与响应校验全部交给 NModbus；
// 这里只保留两件必须自研的事：
//   1) 值层：按测点数据类型/字节序解析与编码寄存器（ModbusValueCodec）；
//   2) 安全：Transport.Retries = 0 —— 一次失败即抛，写操作绝不自动重试。
public abstract class ModbusMasterDriver : IDeviceDriver
{
    protected static readonly IModbusFactory Factory = new ModbusFactory();
    private readonly SemaphoreSlim _wire = new(1, 1);
    private IModbusMaster? _master;

    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }

    protected abstract IModbusMaster Create(Device device);

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        await _wire.WaitAsync(cancellationToken);
        try
        {
            Disconnect();
            var master = await Task.Run(() => Create(device), cancellationToken);
            if (master.Transport is ModbusTransport transport)
            {
                transport.Retries = 0;
                transport.ReadTimeout = Math.Max(100, device.TimeoutMs);
                transport.WriteTimeout = Math.Max(100, device.TimeoutMs);
            }
            _master = master; IsConnected = true; LastError = null;
        }
        catch (Exception ex) { IsConnected = false; LastError = ex.Message; throw; }
        finally { _wire.Release(); }
    }

    public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<RawTagValue>>(() => ReadCore(device, plan, cancellationToken), cancellationToken);

    private IReadOnlyList<RawTagValue> ReadCore(Device device, ReadPlan plan, CancellationToken ct)
    {
        var master = _master ?? throw new IOException("Modbus 未连接");
        var result = new List<RawTagValue>();
        foreach (var block in plan.Blocks)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var function = ModbusValueCodec.FunctionCode(block.Area);
                var start = checked((ushort)block.StartAddress);
                var length = checked((ushort)block.Length);
                var bytes = function <= 2
                    ? ToBits(function == 1
                        ? master.ReadCoils(device.StationNumber, start, length)
                        : master.ReadInputs(device.StationNumber, start, length))
                    : ToRegisters(function == 3
                        ? master.ReadHoldingRegisters(device.StationNumber, start, length)
                        : master.ReadInputRegisters(device.StationNumber, start, length));
                foreach (var item in block.Tags)
                {
                    var value = block.Area is TagArea.Coil or TagArea.DiscreteInput
                        ? (ModbusValueCodec.DecodeCoil(bytes, item.Offset) ? 1d : 0d)
                        : ModbusValueCodec.DecodeRegister(bytes, item.Offset, item.DataType, item.ByteOrder);
                    if (!double.IsFinite(value)) throw new IOException($"测点 #{item.TagId} 返回非有限值");
                    result.Add(new(item.TagId, value));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                IsConnected = false;
                LastError = $"{block.Area} 地址 {block.StartAddress}，长度 {block.Length}：{ex.Message}";
                throw new IOException(LastError, ex);
            }
        }
        IsConnected = true; LastError = null;
        return result;
    }

    public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!tag.Writable || tag.Area is not (TagArea.Coil or TagArea.HoldingRegister)) throw new InvalidOperationException("测点只读");
        var master = _master ?? throw new IOException("Modbus 未连接");
        try
        {
            var address = checked((ushort)tag.Address);
            if (tag.Area == TagArea.Coil)
            {
                master.WriteSingleCoil(device.StationNumber, address, value != 0);
            }
            else
            {
                var bytes = ModbusValueCodec.EncodeRegister(value, tag.DataType, device.ByteOrder);
                master.WriteMultipleRegisters(device.StationNumber, address, ToWords(bytes));
            }
            LastError = null;
        }
        catch (Exception ex) { IsConnected = false; LastError = ex.Message; throw; }
    }, cancellationToken);

    public virtual void Disconnect()
    {
        var master = Interlocked.Exchange(ref _master, null);
        try { master?.Dispose(); } catch { /* 关闭异常忽略：串口可能已被拔出或端口已失效 */ }
        IsConnected = false;
    }

    public ValueTask DisposeAsync() { Disconnect(); _wire.Dispose(); return ValueTask.CompletedTask; }

    // 寄存器按 Modbus 大端语义转成字节块，复用 ModbusValueCodec 的字节序处理。
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

    // 线圈位序与 Modbus 一致：LSB 优先，bit i 位于 byte i/8 的第 i%8 位。
    private static byte[] ToBits(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++) if (bits[i]) bytes[i / 8] |= (byte)(1 << (i % 8));
        return bytes;
    }
}