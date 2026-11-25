using Scada.Core.Enums;
using Scada.Server.External.Slave;

namespace Scada.Server.Tests;

public class SimulationExportQualityTests
{
    [Fact]
    public void SimulationExportQuality_NeverStaysGood()
    {
        Assert.Equal(TagQuality.Uncertain, ExportPointMap.NormalizeSimulationQuality(TagQuality.Good));
        Assert.Equal(TagQuality.Uncertain, ExportPointMap.NormalizeSimulationQuality(TagQuality.Uncertain));
        Assert.Equal(TagQuality.Bad, ExportPointMap.NormalizeSimulationQuality(TagQuality.Bad));
        Assert.Equal(TagQuality.CommFailure, ExportPointMap.NormalizeSimulationQuality(TagQuality.CommFailure));
    }
}
