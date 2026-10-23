using Opc.Ua;
using Opc.Ua.Client;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector.Opc;

public sealed class OpcUaDriver(OpcUaRuntime runtime) : IDeviceDriver
{
    private ISession? _session;
    public bool IsConnected => _session?.Connected == true;
    public string? LastError { get; private set; }
    public async Task ConnectAsync(Device device, CancellationToken cancellationToken = default)
    {
        Disconnect();
        try { using var timeout = Deadline(device, cancellationToken); _session = await runtime.ConnectAsync(device, timeout.Token); LastError = null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { LastError = "OPC UA 连接超时"; throw new TimeoutException(LastError); }
        catch (Exception ex) { LastError = ex.Message; throw; }
    }

    public static NodeId ResolveNode(string address, NamespaceTable namespaces)
    {
        if (!OpcNodeAddress.IsValid(address)) throw new IOException("OPC UA NodeId 格式无效");
        var expanded = ExpandedNodeId.Parse(address);
        var node = ExpandedNodeId.ToNodeId(expanded, namespaces, false);
        if (node.IsNull) throw new IOException("NodeId 的命名空间 URI 不在服务器的命名空间表中");
        return node;
    }

    public async Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new IOException("OPC UA 尚未连接");
        var values = new List<RawTagValue>();
        try
        {
            foreach (var batch in plan.Tags.Chunk(100))
            {
                using var timeout = Deadline(device, cancellationToken);
                var valid = new List<(Tag Tag, NodeId Node)>();
                foreach (var tag in batch)
                {
                    try { valid.Add((tag, ResolveNode(tag.NodeId, session.NamespaceUris))); }
                    catch (Exception ex) { values.Add(new(tag.Id, 0, TagQuality.Bad, null, ex.Message)); }
                }
                if (valid.Count == 0) continue;
                var nodes = new ArrayOf<ReadValueId>(valid.Select(x => new ReadValueId { NodeId = x.Node, AttributeId = Attributes.Value }).ToArray());
                var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both, nodes, timeout.Token);
                if (response.Results.Count != valid.Count) throw new IOException("OPC UA 读取响应数量不匹配");
                for (var i = 0; i < valid.Count; i++) values.Add(Decode(valid[i].Tag, response.Results[i]));
            }
            LastError = values.FirstOrDefault(x => x.Error is not null)?.Error;
            return values;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { Disconnect(); LastError = "OPC UA 读取超时"; throw new TimeoutException(LastError); }
        catch (Exception ex) { LastError = ex.Message; Disconnect(); throw; }
    }

    public static RawTagValue Decode(Tag tag, DataValue value)
    {
        var source = value.SourceTimestamp.ToDateTimeOffset();
        long? sourceTs = source.Year >= 1970 ? source.ToUnixTimeMilliseconds() : null;
        if (!StatusCode.IsGood(value.StatusCode)) return new(tag.Id, 0, StatusCode.IsUncertain(value.StatusCode) ? TagQuality.Uncertain : TagQuality.Bad, sourceTs, value.StatusCode.ToString());
        double? scalar = tag.DataType switch
        {
            TagDataType.Bool => value.WrappedValue.TryGetValue(out bool x) ? x ? 1 : 0 : null,
            TagDataType.Int16 => value.WrappedValue.TryGetValue(out short x) ? x : null,
            TagDataType.UInt16 => value.WrappedValue.TryGetValue(out ushort x) ? x : null,
            TagDataType.Int32 => value.WrappedValue.TryGetValue(out int x) ? x : null,
            TagDataType.UInt32 => value.WrappedValue.TryGetValue(out uint x) ? x : null,
            TagDataType.Float => value.WrappedValue.TryGetValue(out float x) ? x : null,
            TagDataType.Double => value.WrappedValue.TryGetValue(out double x) ? x : null,
            _ => null
        };
        return scalar is double number && double.IsFinite(number) ? new(tag.Id, number, TagQuality.Good, sourceTs)
            : new(tag.Id, 0, TagQuality.Bad, sourceTs, $"服务器值不是所配置的 {tag.DataType} 有限标量");
    }

    public async Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new IOException("OPC UA 尚未连接");
        if (!tag.Writable) throw new InvalidOperationException("测点只读");
        Variant typed = tag.DataType switch { TagDataType.Bool => new Variant(value != 0), TagDataType.Int16 => new Variant(checked((short)value)),
            TagDataType.UInt16 => new Variant(checked((ushort)value)), TagDataType.Int32 => new Variant(checked((int)value)),
            TagDataType.UInt32 => new Variant(checked((uint)value)), TagDataType.Float => new Variant((float)value), TagDataType.Double => new Variant(value),
            _ => throw new InvalidOperationException("不支持写入此数据类型") };
        try
        {
            using var timeout = Deadline(device, cancellationToken);
            var writes = new ArrayOf<WriteValue>(new[] { new WriteValue { NodeId = ResolveNode(tag.NodeId, session.NamespaceUris), AttributeId = Attributes.Value, Value = new DataValue(typed) } });
            var response = await session.WriteAsync(null, writes, timeout.Token);
            if (response.Results.Count != 1 || !StatusCode.IsGood(response.Results[0])) throw new IOException($"OPC UA 写入被拒绝：{response.Results.Span.ToArray().FirstOrDefault()}");
            LastError = null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { Disconnect(); LastError = "OPC UA 写入超时，结果未知"; throw new TimeoutException(LastError); }
        catch (Exception ex) { LastError = ex.Message; throw; }
    }
    private static CancellationTokenSource Deadline(Device device, CancellationToken ct)
    { var source = CancellationTokenSource.CreateLinkedTokenSource(ct); source.CancelAfter(device.TimeoutMs); return source; }
    public void Disconnect() { if (_session is { } session) runtime.Release(session); _session = null; }
    public ValueTask DisposeAsync() { Disconnect(); return ValueTask.CompletedTask; }
}
