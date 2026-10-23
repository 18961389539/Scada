using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Collector.Tests;

public class ReadPlanBuilderTests
{
    private static readonly Device Device = new() { Id = 1, Name = "dev", ByteOrder = ByteOrder.ABCD };

    private static Tag Float(int id, int address, TagArea area = TagArea.HoldingRegister) => new()
    {
        Id = id,
        DeviceId = 1,
        Name = $"t{id}",
        Address = address,
        Area = area,
        DataType = TagDataType.Float
    };

    [Fact]
    public void NearbyRegisters_AreMergedIntoOneBlock()
    {
        var plan = new ReadPlanBuilder().Build(Device, new[] { Float(1, 0), Float(2, 5), Float(3, 10) });
        var block = Assert.Single(plan.Blocks);
        Assert.Equal(0, block.StartAddress);
        Assert.Equal(12, block.Length);
        Assert.Equal(new[] { 0, 5, 10 }, block.Tags.Select(x => x.Offset));
    }

    [Fact]
    public void RegistersFarApart_AreSplit()
    {
        var plan = new ReadPlanBuilder().Build(Device, new[] { Float(1, 0), Float(2, 40) });
        Assert.Equal(2, plan.Blocks.Count);
        Assert.Equal(0, plan.Blocks[0].StartAddress);
        Assert.Equal(40, plan.Blocks[1].StartAddress);
    }

    [Fact]
    public void BlockLength_DoesNotExceedModbusLimit()
    {
        var tags = Enumerable.Range(0, 100).Select(i => Float(i + 1, i * 2)).ToList();
        var plan = new ReadPlanBuilder().Build(Device, tags);

        Assert.True(plan.Blocks.Count > 1);
        Assert.All(plan.Blocks, block => Assert.True(block.Length <= 125));
    }

    [Fact]
    public void DifferentAreas_ArePlannedSeparately()
    {
        var plan = new ReadPlanBuilder().Build(Device, new[]
        {
            Float(1, 0, TagArea.HoldingRegister),
            Float(2, 2, TagArea.InputRegister),
            new Tag { Id = 3, DeviceId = 1, Name = "coil", Area = TagArea.Coil, Address = 0, DataType = TagDataType.Bool }
        });

        Assert.Equal(3, plan.Blocks.Count);
        Assert.Equal(new[] { TagArea.Coil, TagArea.HoldingRegister, TagArea.InputRegister },
            plan.Blocks.Select(x => x.Area).ToArray());
    }

    [Fact]
    public void Tags_AreOrderedByAddress()
    {
        var plan = new ReadPlanBuilder().Build(Device, new[] { Float(1, 20), Float(2, 0), Float(3, 10) });
        Assert.All(plan.Blocks, block =>
            Assert.Equal(block.Tags.Select(x => x.Offset).OrderBy(x => x), block.Tags.Select(x => x.Offset)));
        Assert.Equal(0, plan.Blocks[0].Tags[0].Offset);
    }
}
