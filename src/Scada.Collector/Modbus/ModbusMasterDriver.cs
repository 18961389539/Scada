using NModbus;
using NModbus.IO;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;

using Scada.Protocols.Modbus;

namespace Scada.Collector.Modbus;

// 基于 NModbus 的 Modbus 驱动基类。
// 帧层（RTU CRC / ASCII LRC / TCP MBAP）、从站异常码解析与响应校验全部交给 NModbus；
// 这里只保留两件必须自研的事：
//   1) 值层：按测点数据类型/字节序解析与编码寄存器（ModbusValueCodec）；
//   2) 安全：Transport.Retries = 0 —— 一次失败即抛，写操作绝不自动重试。
public abstract class ModbusMasterDriver : IDeviceDriver, ITransactionGatedDriver
{
    protected static readonly IModbusFactory Factory = new ModbusFactory();
    private readonly SemaphoreSlim _wire = new(1, 1);
    private IModbusMaster? _master;
    private ITransactionGate? _gate;

    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// 共享通道的总线锁。注意粒度是「一个请求/响应」，不是「一份读取计划」：
    /// 一份计划可能有几十个读块，整份加锁会让一台设备独占总线数百毫秒，
    /// 把同总线上按 100ms 采集的设备一起拖慢。未绑定通道时为 null，不加任何额外约束。
    /// </summary>
    public ITransactionGate? TransactionGate { set => _gate = value; }

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
                // 地址换算放在事务外：算错了不该去抢一次总线。
                var bytes = Transact(device.Id, () => ReadBlockBytes(master, device, function, start, length), ct);
                var link = DeviceLink.From(device);
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
                LastError = $"{block.Area} 地址 {block.StartAddress}，长度 {block.Length}：{ex.Message}";
                // 从站返回异常码说明链路是通的，只是拒绝了这次请求（地址越界、寄存器不存在等）。
                // 只有真正的传输层故障才把链路标记为断开——共享通道时代，一个配错的地址
                // 不该让同一总线上的其它设备全部重连。
                if (!IsSlaveRejection(ex)) IsConnected = false;
                throw new IOException(LastError, ex);
            }
        }
        IsConnected = true; LastError = null;
        return result;
    }

    private byte[] ReadBlockBytes(IModbusMaster master, Device device, ushort function, ushort start, ushort length)
    {
        var link = DeviceLink.From(device);
        return function <= 2
            ? ToBits(function == 1
                ? master.ReadCoils(link.Station, start, length)
                : master.ReadInputs(link.Station, start, length))
            : ToRegisters(function == 3
                ? master.ReadHoldingRegisters(link.Station, start, length)
                : master.ReadInputRegisters(link.Station, start, length));
    }

    // 单次总线事务。未绑定通道时直接执行，行为与共享通道之前完全一致。
    private T Transact<T>(int deviceId, Func<T> operation, CancellationToken cancellationToken) =>
        _gate is null ? operation() : _gate.Execute(deviceId, operation, cancellationToken);

    public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        if (!tag.Writable || tag.Area is not (TagArea.Coil or TagArea.HoldingRegister)) throw new InvalidOperationException("测点只读");
        var master = _master ?? throw new IOException("Modbus 未连接");
        try
        {
            // 写命令也按单事务排队：写比读更该尽快落到总线上，不能被同通道的长读取计划压住。
            Transact(device.Id, () => WriteCore(master, device, tag, value), cancellationToken);
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (!IsSlaveRejection(ex)) IsConnected = false;
            throw;
        }
    }, cancellationToken);

    public virtual void Disconnect()
    {
        var master = Interlocked.Exchange(ref _master, null);
        try { master?.Dispose(); } catch { /* 关闭异常忽略：串口可能已被拔出或端口已失效 */ }
        IsConnected = false;
    }

    private static bool WriteCore(IModbusMaster master, Device device, Tag tag, double value)
    {
        var link = DeviceLink.From(device);
        var address = checked((ushort)tag.Address);
        if (tag.Area == TagArea.Coil)
        {
            // 位写用 WriteSingleCoil 而不是读改写整段寄存器：后者会覆盖同段其它位。
            master.WriteSingleCoil(link.Station, address, value != 0);
        }
        else
        {
            var bytes = ModbusValueCodec.EncodeRegister(value, tag.DataType, link.ByteOrder);
            master.WriteMultipleRegisters(link.Station, address, ToWords(bytes));
        }
        return true;
    }

    public ValueTask DisposeAsync() { Disconnect(); _wire.Dispose(); return ValueTask.CompletedTask; }

    // 从站异常码（SlaveException）代表"设备在线但拒绝了请求"，与链路断开是两件事。
    // 内层异常一起检查：部分调用路径会被库或驱动包装一层。
    private static bool IsSlaveRejection(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SlaveException) return true;
        return false;
    }

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