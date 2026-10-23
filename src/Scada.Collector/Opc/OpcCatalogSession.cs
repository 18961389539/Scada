using Opc.Ua;
using Opc.Ua.Client;
using Scada.Core.Enums;

namespace Scada.Collector.Opc;

public sealed record OpcNodeInfo(string NodeId, string DisplayName, string NodeClass, TagDataType? DataType, bool Readable, bool Writable, string? Error);
public sealed record OpcBrowsePage(IReadOnlyList<OpcNodeInfo> Nodes, ByteString Continuation);

public sealed class OpcCatalogSession(OpcUaRuntime runtime, ISession session) : IAsyncDisposable
{
    public async Task<OpcBrowsePage> BrowseAsync(string nodeId, int limit, ByteString continuation, CancellationToken ct)
    {
        BrowseResult result;
        if (continuation.IsNull || continuation.IsEmpty)
        {
            var descriptions = new ArrayOf<BrowseDescription>(new[] { new BrowseDescription
            {
                NodeId = OpcUaDriver.ResolveNode(nodeId, session.NamespaceUris), BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences, IncludeSubtypes = true,
                NodeClassMask = (uint)(NodeClass.Object | NodeClass.Variable), ResultMask = (uint)BrowseResultMask.All
            } });
            var response = await session.BrowseAsync(null, null, (uint)limit, descriptions, ct);
            if (response.Results.Count != 1) throw new IOException("OPC UA 浏览响应数量不匹配");
            result = response.Results[0];
        }
        else
        {
            var response = await session.BrowseNextAsync(null, false, new ArrayOf<ByteString>(new[] { continuation }), ct);
            if (response.Results.Count != 1) throw new IOException("OPC UA 分页响应数量不匹配");
            result = response.Results[0];
        }
        if (!StatusCode.IsGood(result.StatusCode)) throw new IOException($"浏览失败：{result.StatusCode}");
        var references = result.References.Span.ToArray().Where(x => x.NodeId.ServerIndex == 0).ToArray();
        var variables = references.Where(x => x.NodeClass == NodeClass.Variable).Select(x => Portable(ExpandedNodeId.ToNodeId(x.NodeId, session.NamespaceUris, false))).ToList();
        var metadata = (await MetadataAsync(variables, ct)).GroupBy(x => x.NodeId).ToDictionary(x => x.Key, x => x.First());
        var nodes = new List<OpcNodeInfo>();
        foreach (var reference in references)
        {
            var id = Portable(ExpandedNodeId.ToNodeId(reference.NodeId, session.NamespaceUris, false));
            nodes.Add(reference.NodeClass == NodeClass.Variable ? metadata[id] : new(id, reference.DisplayName.ToString(), "Object", null, false, false, null));
        }
        return new(nodes, result.ContinuationPoint);
    }

    public async Task<IReadOnlyList<OpcNodeInfo>> MetadataAsync(IReadOnlyList<string> addresses, CancellationToken ct)
    {
        var result = new List<OpcNodeInfo>();
        foreach (var group in addresses.Chunk(25))
        {
            var ids = group.Select(x => OpcUaDriver.ResolveNode(x, session.NamespaceUris)).ToArray();
            var attributes = new uint[] { Attributes.DisplayName, Attributes.DataType, Attributes.ValueRank, Attributes.UserAccessLevel };
            var reads = ids.SelectMany(id => attributes.Select(attribute => new ReadValueId { NodeId = id, AttributeId = attribute })).ToArray();
            var response = await session.ReadAsync(null, 0, TimestampsToReturn.Neither, new ArrayOf<ReadValueId>(reads), ct);
            if (response.Results.Count != reads.Length) throw new IOException("OPC UA 变量属性响应数量不匹配");
            for (var i = 0; i < ids.Length; i++)
            {
                var offset = i * 4; var name = response.Results[offset].WrappedValue.ToString();
                var typeValue = response.Results[offset + 1]; var rankValue = response.Results[offset + 2]; var accessValue = response.Results[offset + 3];
                var type = typeValue.WrappedValue.TryGetValue(out NodeId typeId) ? MapType(typeId) : null;
                var scalar = rankValue.WrappedValue.TryGetValue(out int rank) && rank == ValueRanks.Scalar;
                var access = accessValue.WrappedValue.TryGetValue(out byte level) ? level : (byte)0;
                var good = Enumerable.Range(0, 4).All(j => StatusCode.IsGood(response.Results[offset + j].StatusCode));
                var readable = good && (access & AccessLevels.CurrentRead) != 0;
                var error = !good ? $"变量属性不可读：{Enumerable.Range(0, 4).Select(j => response.Results[offset + j].StatusCode).First(x => !StatusCode.IsGood(x))}" : !scalar ? "仅支持标量，数组和结构体暂不支持" : type is null ? $"暂不支持数据类型 {typeId}" : !readable ? "当前用户无读取权限" : null;
                result.Add(new(Portable(ids[i]), name, "Variable", scalar && good ? type : null, readable, good && (access & AccessLevels.CurrentWrite) != 0, error));
            }
        }
        return result;
    }

    private string Portable(NodeId node) => NodeId.ToExpandedNodeId(node, session.NamespaceUris).ToString();
    public static TagDataType? MapType(NodeId type) => type == DataTypeIds.Boolean ? TagDataType.Bool : type == DataTypeIds.Int16 ? TagDataType.Int16
        : type == DataTypeIds.UInt16 ? TagDataType.UInt16 : type == DataTypeIds.Int32 ? TagDataType.Int32 : type == DataTypeIds.UInt32 ? TagDataType.UInt32
        : type == DataTypeIds.Float ? TagDataType.Float : type == DataTypeIds.Double ? TagDataType.Double : null;

    public async ValueTask DisposeAsync()
    {
        try { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await session.CloseAsync(deadline.Token); }
        catch (Exception) { /* Disposing the transport below also releases a failed browsing session. */ }
        finally { runtime.Release(session); }
    }
}
