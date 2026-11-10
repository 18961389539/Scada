using Scada.Core.Enums;

namespace Scada.Core.Opc;

public sealed record OpcNodeInfo(string NodeId, string DisplayName, string NodeClass, TagDataType? DataType, bool Readable, bool Writable, string? Error);
public sealed record OpcBrowseRequest(string NodeId = "i=85", int Limit = 100, string? Cursor = null);
public sealed record OpcBrowseResponse(IReadOnlyList<OpcNodeInfo> Nodes, string? Cursor);
