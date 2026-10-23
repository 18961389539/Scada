using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;

namespace Scada.Engine.Tests;

public class CommandValuePolicyTests
{
    [Theory]
    [InlineData(0.1, 0, 20, 200)]
    [InlineData(2, 10, 30, 10)]
    [InlineData(-2, 10, 0, 5)]
    public void ConvertsEngineeringValueBackToDeviceRegisters(double scale, double offset, double value, double expected)
    {
        var tag = new Tag { Scale = scale, Offset = offset, DataType = TagDataType.Int16 };
        Assert.True(CommandValuePolicy.TryGetRawValue(tag, value, out var raw, out _));
        Assert.Equal(expected, raw);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0.5)]
    [InlineData(0.1, 10000)]
    public void RejectsUnrepresentableOrOutOfRangeRawValues(double scale, double value)
    {
        Assert.False(CommandValuePolicy.TryGetRawValue(new Tag { Scale = scale, DataType = TagDataType.Int16 }, value, out _, out _));
    }

    [Fact]
    public void RejectsReadbackMismatchAndNonFiniteResults()
    {
        var tag = new Tag { DataType = TagDataType.Float };
        Assert.True(CommandValuePolicy.Matches(tag, 20.0000001, 20));
        Assert.False(CommandValuePolicy.Matches(tag, 1_000_000_000, 1_000_000_500));
        Assert.False(CommandValuePolicy.Matches(tag, 20, 2));
        Assert.False(CommandValuePolicy.Matches(tag, 20, double.NaN));
        Assert.False(CommandValuePolicy.TryGetRawValue(tag, double.PositiveInfinity, out _, out _));
    }
}
