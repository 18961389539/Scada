using System.Net.Sockets;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;
using PlcException = libplctag.LibPlcTagException;
using PlcProtocol = libplctag.Protocol;
using PlcStatus = libplctag.Status;
using PlcTag = libplctag.Tag;
using PlcType = libplctag.PlcType;

namespace Scada.Collector.EtherNetIp;

/// <summary>
/// EtherNet/IP CIP 显式报文驱动（libplctag 1.5.2，MPL-2.0/LGPL 2+ 双许可）。
/// 支持 Allen-Bradley ControlLogix/CompactLogix、Micro800、SLC 500、PLC-5、MicroLogix 与 Omron NJ/NX 的命名标签。
/// 测点地址即 PLC 标签名；Bool 使用 BOOL 标签或 PCCC 位地址，其它类型按配置的数据类型选择访问器。
/// </summary>
public sealed class EtherNetIpDriver : IDeviceDriver
{
    private sealed record HandleKey(string Address, TagDataType Type);

    private readonly Dictionary<HandleKey, PlcTag> _handles = new();
    private readonly SemaphoreSlim _io = new(1, 1);
    private EtherNetIpDeviceOptions _options = new();
    private bool _connected;

    public bool IsConnected => _connected;
    public string? LastError { get; private set; }
    public EtherNetIpIdentity? Identity { get; private set; }

    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        _options = ProtocolCatalog.Options<EtherNetIpDeviceOptions>(device);
        Identity = null;
        try
        {
            // 先确认 TCP 44818 可达，再用 ListIdentity 读取设备身份；部分设备只支持广播发现，
            // 因此身份读取失败不阻止连接，由首次标签读取给出最终结论。
            using var probe = new TcpClient();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Clamp(device.TimeoutMs, 500, 30000));
            await probe.ConnectAsync(device.Host, device.Port, deadline.Token);
            _connected = true;
            LastError = null;
            try { Identity = await EtherNetIpIdentityReader.ReadAsync(device.Host, device.Port, device.TimeoutMs, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { Identity = null; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Disconnect();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = "EtherNet/IP 连接超时";
            throw new TimeoutException(LastError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            Disconnect();
            LastError = ex.Message;
            throw;
        }
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        if (!_connected) throw new IOException(LastError ?? "EtherNet/IP 未连接");
        if (plan.Tags.Count == 0) return [];

        var values = new List<RawTagValue>(plan.Tags.Count);
        var deviceFailures = 0;
        string? firstError = null;
        await _io.WaitAsync(cancellationToken);
        try
        {
            for (var i = 0; i < plan.Tags.Count; i++)
            {
                var tag = plan.Tags[i];
                PlcTag? handle = null;
                try
                {
                    handle = GetHandle(device, tag);
                    if (!handle.IsInitialized) await handle.InitializeAsync(cancellationToken);
                    await handle.ReadAsync(cancellationToken);
                    values.Add(Decode(tag, handle));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var status = handle is null ? PlcStatus.ErrorBadStatus : StatusOf(handle);
                    var message = Describe(status, ex);
                    values.Add(new RawTagValue(tag.Id, 0, TagQuality.Bad, null, message));
                    firstError ??= message;
                    var deviceLevel = IsDeviceLevel(status) || ex is IOException or SocketException or TimeoutException;
                    if (!deviceLevel) continue;
                    // 连接级故障不再逐个标签等待超时，剩余测点直接标记为坏质量。
                    deviceFailures++;
                    for (var rest = i + 1; rest < plan.Tags.Count; rest++)
                        values.Add(new RawTagValue(plan.Tags[rest].Id, 0, TagQuality.Bad, null, message));
                    break;
                }
            }
        }
        finally { _io.Release(); }

        if (values.All(value => value.Quality == TagQuality.Bad) && deviceFailures > 0)
        {
            var error = firstError ?? "EtherNet/IP 读取全部失败";
            Close(error);
            throw new IOException(error);
        }
        LastError = firstError;
        return values;
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        if (!_connected) throw CommandFailure.Tag(new IOException("EtherNet/IP 未连接，命令未下发"), GatewayCommandOutcome.NotSent);
        await _io.WaitAsync(cancellationToken);
        PlcTag? handle = null;
        var writeStarted = false;
        try
        {
            handle = GetHandle(device, tag);
            if (!handle.IsInitialized) await handle.InitializeAsync(cancellationToken);
            // 先读回完整缓冲区：BOOL 写位和数组标签都依赖原值，避免把未读区域写成零。
            await handle.ReadAsync(cancellationToken);
            if (tag.DataType == TagDataType.Bool) handle.SetBit(0, value != 0);
            else SetValue(handle, tag.DataType, value);
            writeStarted = true;
            await handle.WriteAsync(cancellationToken);
            LastError = null;
        }
        catch (PlcException ex)
        {
            var status = handle is null ? PlcStatus.ErrorBadStatus : StatusOf(handle);
            LastError = Describe(status, ex);
            var outcome = writeStarted && status is (PlcStatus.ErrorTimeout or PlcStatus.ErrorBusy or PlcStatus.ErrorPartial
                or PlcStatus.ErrorBadConnection or PlcStatus.ErrorBadGateway)
                ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent;
            throw CommandFailure.Tag(new IOException(LastError, ex), outcome);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LastError = writeStarted ? "EtherNet/IP 写入超时，结果未知" : "EtherNet/IP 写入准备超时，命令未下发";
            throw CommandFailure.Tag(new TimeoutException(LastError), writeStarted ? GatewayCommandOutcome.Unknown : GatewayCommandOutcome.NotSent);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastError = ex.Message;
            throw CommandFailure.Tag(new InvalidOperationException($"EtherNet/IP 写入未执行：{ex.Message}", ex), GatewayCommandOutcome.NotSent);
        }
        finally { _io.Release(); }
    }

    private PlcTag GetHandle(Device device, Tag tag)
    {
        var address = tag.NodeId.Trim();
        var key = new HandleKey(address, tag.DataType);
        if (_handles.TryGetValue(key, out var existing)) return existing;
        var handle = new PlcTag
        {
            Name = address,
            Gateway = device.Host,
            Protocol = PlcProtocol.ab_eip,
            PlcType = MapPlcType(_options.PlcType),
            Timeout = TimeSpan.FromMilliseconds(device.TimeoutMs),
            AllowPacking = _options.AllowPacking,
            ElementCount = 1
        };
        var path = (_options.Path ?? "").Trim();
        if (path.Length > 0) handle.Path = path;
        // Micro800 必须使用 CIP 连接；校验层已要求它的背板路由为空。
        if (_options.PlcType == "Micro800") handle.UseConnectedMessaging = true;
        _handles[key] = handle;
        return handle;
    }

    private static RawTagValue Decode(Tag tag, PlcTag handle)
    {
        var size = handle.GetSize();
        var expected = ExpectedSize(tag.DataType);
        if (expected > 0 && size < expected)
            return new RawTagValue(tag.Id, 0, TagQuality.Bad, null,
                $"PLC 标签元素大小为 {size} 字节，小于配置的 {tag.DataType} 所需 {expected} 字节");
        var value = tag.DataType switch
        {
            TagDataType.Bool => handle.GetBit(0) ? 1 : 0,
            TagDataType.Int16 => handle.GetInt16(0),
            TagDataType.UInt16 => handle.GetUInt16(0),
            TagDataType.Int32 => handle.GetInt32(0),
            TagDataType.UInt32 => handle.GetUInt32(0),
            TagDataType.Float => handle.GetFloat32(0),
            TagDataType.Double => handle.GetFloat64(0),
            _ => double.NaN
        };
        return double.IsFinite(value) ? new RawTagValue(tag.Id, value) : new RawTagValue(tag.Id, 0, TagQuality.Bad, null, "PLC 返回非有限数值");
    }

    private static void SetValue(PlcTag handle, TagDataType type, double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("写入值不是有限数值");
        switch (type)
        {
            case TagDataType.Int16: handle.SetInt16(0, checked((short)value)); break;
            case TagDataType.UInt16: handle.SetUInt16(0, checked((ushort)value)); break;
            case TagDataType.Int32: handle.SetInt32(0, checked((int)value)); break;
            case TagDataType.UInt32: handle.SetUInt32(0, checked((uint)value)); break;
            case TagDataType.Float:
                var single = (float)value;
                if (!float.IsFinite(single)) throw new InvalidOperationException("写入值超出 REAL 范围");
                handle.SetFloat32(0, single);
                break;
            case TagDataType.Double: handle.SetFloat64(0, value); break;
            default: throw new InvalidOperationException($"不支持写入数据类型 {type}");
        }
    }

    private static int ExpectedSize(TagDataType type) => type switch
    {
        TagDataType.Int16 or TagDataType.UInt16 => 2,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float => 4,
        TagDataType.Double => 8,
        _ => 0
    };

    private static PlcType MapPlcType(string value) => value switch
    {
        "CompactLogix" or "ControlLogix" => PlcType.ControlLogix,
        "Micro800" => PlcType.Micro800,
        "Slc500" => PlcType.Slc500,
        "Plc5" => PlcType.Plc5,
        "LogixPccc" => PlcType.LogixPccc,
        "MicroLogix" => PlcType.MicroLogix,
        "Omron" => PlcType.Omron,
        _ => throw new InvalidOperationException($"不支持的 PLC 类型 {value}")
    };

    private static PlcStatus StatusOf(PlcTag handle)
    {
        try { return handle.GetStatus(); }
        catch { return PlcStatus.ErrorBadStatus; }
    }

    private static string Describe(PlcStatus status, Exception exception)
    {
        var code = status.ToString().Replace("Error", "", StringComparison.Ordinal);
        return $"EtherNet/IP {code}: {exception.Message}";
    }

    private static bool IsDeviceLevel(PlcStatus status) => status is
        PlcStatus.ErrorBadConnection or PlcStatus.ErrorBadGateway or PlcStatus.ErrorTimeout
        or PlcStatus.ErrorOpen or PlcStatus.ErrorWinsock or PlcStatus.ErrorNoResources
        or PlcStatus.ErrorNoMem or PlcStatus.ErrorBadDevice or PlcStatus.ErrorThreadCreate;

    private void Close(string reason)
    {
        foreach (var handle in _handles.Values)
        {
            try { handle.Dispose(); } catch { }
        }
        _handles.Clear();
        _connected = false;
        LastError = reason;
    }

    public void Disconnect() => Close("已断开");

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }
}
