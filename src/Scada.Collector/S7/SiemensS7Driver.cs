using global::S7.Net;
using Scada.Collector.Modbus;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.S7;

public sealed class SiemensS7Driver : IDeviceDriver
{
    private Plc? _client;
    public bool IsConnected => _client?.IsConnected == true;
    public string? LastError { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        var cpu = device.Cpu switch { S7Cpu.S7200 => CpuType.S7200, S7Cpu.S7300 => CpuType.S7300, S7Cpu.S7400 => CpuType.S7400,
            S7Cpu.S71200 => CpuType.S71200, S7Cpu.S71500 => CpuType.S71500, _ => throw new ArgumentOutOfRangeException(nameof(device.Cpu)) };
        var client = new Plc(cpu, device.Host, device.Port, device.Rack, device.Slot) { ReadTimeout = device.TimeoutMs, WriteTimeout = device.TimeoutMs };
        try
        {
            using var deadline = Deadline(device, cancellationToken);
            using var registration = deadline.Token.Register(client.Close);
            await client.OpenAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            _client = client; LastError = null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { LastError = "S7 操作超时"; Disconnect(); throw new TimeoutException(LastError, ex); }
        catch (Exception ex) { client.Close(); LastError = ex.Message; throw; }
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("设备尚未连接");
        var values = new List<RawTagValue>();
        var address = "";
        try
        {
            foreach (var block in plan.Blocks)
            {
                address = $"{block.Area} DB {block.DbNumber} 字节 {block.StartAddress} 长度 {block.Length}";
                using var deadline = Deadline(device, cancellationToken);
                using var registration = deadline.Token.Register(client.Close);
                var bytes = await client.ReadBytesAsync(Area(block.Area), block.Area == TagArea.DataBlock ? block.DbNumber : 0,
                    block.StartAddress, block.Length, deadline.Token);
                foreach (var item in block.Tags)
                {
                    var value = item.DataType == TagDataType.Bool ? ((bytes[item.Offset] >> item.BitOffset) & 1)
                        : ModbusValueCodec.DecodeRegister(bytes.AsSpan(item.Offset), 0, item.DataType, ByteOrder.ABCD);
                    if (!double.IsFinite(value)) throw new IOException($"测点 #{item.TagId} 返回非有限值");
                    values.Add(new RawTagValue(item.TagId, value));
                }
            }
            LastError = null;
            return values;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { LastError = "S7 操作超时"; Disconnect(); throw new TimeoutException(LastError, ex); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { Disconnect(); throw; }
        catch (Exception ex) { LastError = $"{address}：{ex.Message}"; Disconnect(); throw new IOException(LastError, ex); }
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        var client = _client ?? throw new InvalidOperationException("设备尚未连接");
        if (!tag.Writable || tag.Area == TagArea.Input) throw new InvalidOperationException("测点只读");
        try
        {
            using var deadline = Deadline(device, cancellationToken);
            using var registration = deadline.Token.Register(client.Close);
            var db = tag.Area == TagArea.DataBlock ? tag.DbNumber : 0;
            // A bit write uses S7's native bit operation so adjacent PLC bits cannot be overwritten.
            if (tag.DataType == TagDataType.Bool)
                await client.WriteBitAsync(Area(tag.Area), db, tag.Address, tag.BitOffset, value != 0, deadline.Token);
            else
                await client.WriteBytesAsync(Area(tag.Area), db, tag.Address,
                    ModbusValueCodec.EncodeRegister(value, tag.DataType, ByteOrder.ABCD), deadline.Token);
            LastError = null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested) { LastError = "S7 操作超时"; Disconnect(); throw new TimeoutException(LastError, ex); }
        catch (Exception ex) { LastError = ex.Message; Disconnect(); throw; }
    }

    private static global::S7.Net.DataType Area(TagArea area) => area switch
    {
        TagArea.DataBlock => global::S7.Net.DataType.DataBlock, TagArea.Memory => global::S7.Net.DataType.Memory,
        TagArea.Input => global::S7.Net.DataType.Input, TagArea.Output => global::S7.Net.DataType.Output,
        _ => throw new InvalidOperationException("S7 区域无效")
    };
    private static CancellationTokenSource Deadline(Device device, CancellationToken ct)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(ct); source.CancelAfter(device.TimeoutMs); return source; }
    public void Disconnect() { _client?.Close(); _client = null; }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
