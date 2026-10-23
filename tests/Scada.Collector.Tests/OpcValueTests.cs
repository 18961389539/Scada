using Opc.Ua;
using Scada.Collector.Opc;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Collector.Tests;

public class OpcValueTests
{
    [Fact]
    public void QualityAndSourceTimestampArePreservedAndInvalidScalarsAreRejected()
    {
        var tag = new Tag { Id = 1, DeviceId = 1, Name = "real", Area = TagArea.OpcNode, NodeId = "nsu=urn:plant;s=Temperature", DataType = TagDataType.Float };
        var value = new DataValue(new Variant(12.5f), StatusCodes.Good, DateTimeUtc.Now);
        var decoded = OpcUaDriver.Decode(tag, value); Assert.Equal(12.5, decoded.Value); Assert.Equal(TagQuality.Good, decoded.Quality); Assert.NotNull(decoded.SourceTs);
        Assert.Equal(TagQuality.Uncertain, OpcUaDriver.Decode(tag, value.WithStatus(StatusCodes.Uncertain)).Quality);
        Assert.Equal(TagQuality.Bad, OpcUaDriver.Decode(tag, value.WithStatus(StatusCodes.BadSensorFailure)).Quality);
        Assert.Equal(TagQuality.Bad, OpcUaDriver.Decode(tag, new DataValue(new Variant("12.5"))).Quality);
        Assert.Equal(TagQuality.Bad, OpcUaDriver.Decode(tag, new DataValue(new Variant(double.PositiveInfinity))).Quality);
    }

    [Theory]
    [InlineData("i=85", true)] [InlineData("ns=2;s=Pump.Run", true)] [InlineData("nsu=urn:plant;i=200", true)]
    [InlineData("ns=65536;i=1", false)] [InlineData("i=0", false)] [InlineData("i=-1", false)] [InlineData("s=", false)]
    [InlineData("nsu=;s=Run", false)] [InlineData("ns=2;i=4294967296", false)]
    public void NodeAddressSyntax(string address, bool valid) => Assert.Equal(valid, OpcNodeAddress.IsValid(address));

    [Fact]
    public void NamespaceUrisResolveAcrossDifferentNamespaceIndexes()
    {
        var namespaces = new NamespaceTable(); namespaces.Append("urn:other"); namespaces.Append("urn:plant");
        var node = OpcUaDriver.ResolveNode("nsu=urn:plant;s=Temperature", namespaces);
        Assert.Equal(2, node.NamespaceIndex);
        Assert.ThrowsAny<Exception>(() => OpcUaDriver.ResolveNode("nsu=urn:missing;s=Temperature", namespaces));
    }
}
