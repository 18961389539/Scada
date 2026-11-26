using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;

namespace Scada.Core.Tests;

public class AddressOverlapTests
{
    private static Tag Tag(
        string name, TagArea area, int address, TagDataType type, bool writable,
        int bit = 0, int db = 1, string node = "", int textLen = 0, int id = 0, TagKind kind = TagKind.Physical) => new()
    {
        Id = id,
        Name = name,
        DeviceId = 1,
        Kind = kind,
        Area = area,
        Address = address,
        DataType = type,
        Writable = writable,
        BitOffset = bit,
        DbNumber = db,
        NodeId = node,
        TextLength = textLen
    };

    [Fact]
    public void DifferentBitsOnSameHoldingRegister_AreShared()
    {
        var a = Tag("b0", TagArea.HoldingRegister, 10, TagDataType.Bool, true, bit: 0, id: 1);
        var b = Tag("b1", TagArea.HoldingRegister, 10, TagDataType.Bool, true, bit: 1, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void SameBitBothWritable_Conflicts()
    {
        var a = Tag("b0", TagArea.HoldingRegister, 10, TagDataType.Bool, true, bit: 0, id: 1);
        var b = Tag("alias", TagArea.HoldingRegister, 10, TagDataType.Bool, true, bit: 0, id: 2);
        var hits = AddressOverlap.Conflicts(a, null, [b]);
        Assert.Single(hits);
        Assert.Contains("均可写", hits[0].Reason);
    }

    [Fact]
    public void SameBitReadOnlyMapping_Allowed()
    {
        var a = Tag("w", TagArea.HoldingRegister, 10, TagDataType.Bool, true, bit: 0, id: 1);
        var b = Tag("ro", TagArea.HoldingRegister, 10, TagDataType.Bool, false, bit: 0, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void FloatPartialOverlapWithInt_Conflicts()
    {
        var f = Tag("f", TagArea.HoldingRegister, 0, TagDataType.Float, false, id: 1);
        var i = Tag("i", TagArea.HoldingRegister, 1, TagDataType.Int16, false, id: 2);
        var hits = AddressOverlap.Conflicts(f, null, [i]);
        Assert.Single(hits);
        Assert.Contains("重叠", hits[0].Reason);
    }

    [Fact]
    public void SameFloatBothWritable_Conflicts()
    {
        var a = Tag("a", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 1);
        var b = Tag("b", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 2);
        Assert.Single(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void SameFloatReadOnlyAlias_Allowed()
    {
        var a = Tag("a", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 1);
        var b = Tag("b", TagArea.HoldingRegister, 0, TagDataType.Float, false, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void WordAndBitReadOnlyMapping_Allowed()
    {
        var word = Tag("word", TagArea.HoldingRegister, 10, TagDataType.Int16, true, id: 1);
        var bit = Tag("bit", TagArea.HoldingRegister, 10, TagDataType.Bool, false, bit: 3, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(word, null, [bit]));
    }

    [Fact]
    public void WordAndBitBothWritable_Conflicts()
    {
        var word = Tag("word", TagArea.HoldingRegister, 10, TagDataType.Float, true, id: 1);
        var bit = Tag("bit", TagArea.HoldingRegister, 11, TagDataType.Bool, true, bit: 0, id: 2);
        var hits = AddressOverlap.Conflicts(word, null, [bit]);
        Assert.Single(hits);
        Assert.Contains("均可写", hits[0].Reason);
    }

    [Fact]
    public void SameCoilBothWritable_Conflicts()
    {
        var a = Tag("c1", TagArea.Coil, 5, TagDataType.Bool, true, id: 1);
        var b = Tag("c2", TagArea.Coil, 5, TagDataType.Bool, true, id: 2);
        Assert.Single(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void DifferentCoils_DoNotConflict()
    {
        var a = Tag("c1", TagArea.Coil, 5, TagDataType.Bool, true, id: 1);
        var b = Tag("c2", TagArea.Coil, 6, TagDataType.Bool, true, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void DifferentDataBlocks_DoNotConflict()
    {
        var a = Tag("a", TagArea.DataBlock, 0, TagDataType.Int16, true, db: 1, id: 1);
        var b = Tag("b", TagArea.DataBlock, 0, TagDataType.Int16, true, db: 2, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void S7DifferentBitsSameByte_AreShared()
    {
        var a = Tag("a", TagArea.DataBlock, 4, TagDataType.Bool, true, bit: 0, db: 1, id: 1);
        var b = Tag("b", TagArea.DataBlock, 4, TagDataType.Bool, true, bit: 7, db: 1, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void SameOpcNodeBothWritable_Conflicts()
    {
        var a = Tag("a", TagArea.OpcNode, 0, TagDataType.Float, true, node: "ns=2;s=Tank.Level", id: 1);
        var b = Tag("b", TagArea.OpcNode, 0, TagDataType.Float, true, node: "ns=2;s=Tank.Level", id: 2);
        var hits = AddressOverlap.Conflicts(a, null, [b]);
        Assert.Single(hits);
        Assert.Contains("均可写", hits[0].Reason);
    }

    [Fact]
    public void SameOpcNodeReadOnlyAlias_Allowed()
    {
        var a = Tag("a", TagArea.OpcNode, 0, TagDataType.Float, true, node: "ns=2;s=Tank.Level", id: 1);
        var b = Tag("b", TagArea.OpcNode, 0, TagDataType.Float, false, node: "ns=2;s=Tank.Level", id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }

    [Fact]
    public void MetadataOnlyUpdate_IsGrandfathered()
    {
        var previous = Tag("a", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 1);
        previous.Description = "old";
        var incoming = Tag("a", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 1);
        incoming.Description = "new";
        var other = Tag("b", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(incoming, null, [other], previous));
    }

    [Fact]
    public void BecomingWritable_IsRechecked()
    {
        var previous = Tag("a", TagArea.HoldingRegister, 0, TagDataType.Float, false, id: 1);
        var incoming = Tag("a", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 1);
        var other = Tag("b", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 2);
        Assert.Single(AddressOverlap.Conflicts(incoming, null, [other], previous));
    }

    [Fact]
    public void ConflictsAmong_GrandfathersExistingPair()
    {
        var a = Tag("a", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 1);
        var b = Tag("b", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 2);
        var existing = new HashSet<(int, int)> { (1, 2) };
        Assert.Empty(AddressOverlap.ConflictsAmong([a, b], null, existing));
        Assert.Single(AddressOverlap.ConflictsAmong([a, b], null));
    }

    [Fact]
    public void CalculatedTags_AreIgnored()
    {
        var a = Tag("c", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 1, kind: TagKind.Calculated);
        var b = Tag("p", TagArea.HoldingRegister, 0, TagDataType.Float, true, id: 2);
        Assert.Empty(AddressOverlap.Conflicts(a, null, [b]));
    }
}