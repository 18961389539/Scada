using Scada.Core.Models;
using Scada.Server.Mcp;

namespace Scada.Server.Tests;

public sealed class McpAggregatePointTests
{
    [Fact]
    public void SimulatedOnlyWindowsHaveNullValuesAndKeepSimulatedCount()
    {
        var live = McpAnalysisTools.ToPoint(new AggregatePoint(1, 1_000, 10, 8, 12, 4, 1));
        Assert.Equal(10, live.Avg);
        Assert.Equal(8, live.Min);
        Assert.Equal(12, live.Max);
        Assert.Equal(4, live.Count);
        Assert.Equal(1, live.SimulatedCount);

        var simulated = McpAnalysisTools.ToPoint(new AggregatePoint(1, 2_000, 0, 0, 0, 0, 6));
        Assert.Null(simulated.Avg);
        Assert.Null(simulated.Min);
        Assert.Null(simulated.Max);
        Assert.Equal(0, simulated.Count);
        Assert.Equal(6, simulated.SimulatedCount);
    }
}
