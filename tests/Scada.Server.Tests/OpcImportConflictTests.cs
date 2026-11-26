using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class OpcImportConflictTests
{
    [Fact]
    public void ImportConflicts_RejectsDuplicateNodeIdAgainstExistingAndBatch()
    {
        var device = new Device { Name = "OPC", Protocol = ProtocolType.OpcUa };
        var existing = new Tag { Id = 1, Name = "已有", DeviceId = 1, NodeId = "ns=2;s=T1", Area = TagArea.OpcNode, DataType = TagDataType.Float };
        var incoming = new Tag { Name = "新建", DeviceId = 1, NodeId = "ns=2;s=T1", Area = TagArea.OpcNode, DataType = TagDataType.Float };
        var errors = OpcCatalogService.ImportConflicts([incoming], [existing], device);
        Assert.Contains(errors, e => e.Contains("已存在相同 OPC 变量") && e.Contains("已有"));
        var batch = OpcCatalogService.ImportConflicts([
            new Tag { Name = "A", DeviceId = 1, NodeId = "ns=2;s=T2", Area = TagArea.OpcNode, DataType = TagDataType.Float },
            new Tag { Name = "B", DeviceId = 1, NodeId = "ns=2;s=T2", Area = TagArea.OpcNode, DataType = TagDataType.Float }
        ], [], device);
        Assert.Contains(batch, e => e.Contains("同一变量被重复选择"));
    }

    [Fact]
    public void ImportConflicts_AllowsDistinctNodeIds()
    {
        var device = new Device { Name = "OPC", Protocol = ProtocolType.OpcUa };
        var existing = new Tag { Id = 1, Name = "已有", DeviceId = 1, NodeId = "ns=2;s=T1", Area = TagArea.OpcNode, DataType = TagDataType.Float };
        var incoming = new Tag { Name = "新建", DeviceId = 1, NodeId = "ns=2;s=T2", Area = TagArea.OpcNode, DataType = TagDataType.Float };
        Assert.Empty(OpcCatalogService.ImportConflicts([incoming], [existing], device));
    }
}
