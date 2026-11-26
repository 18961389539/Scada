using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Engine.Control;

namespace Scada.Engine.Tests;

public class ControlSimulationWriteTests
{
    [Fact]
    public void ExternalWriteToSimulatedDevice_IsRefused()
    {
        var device = new Device { Name = "PLC-1", SimulationEnabled = true };
        var reason = ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.External);
        Assert.NotNull(reason);
        Assert.Contains("不会下到现场", reason);
        Assert.Contains("已拒绝", reason);
    }

    [Fact]
    public void OperatorAndSequenceWrites_RemainAllowed()
    {
        var device = new Device { Name = "PLC-1", SimulationEnabled = true };
        Assert.Null(ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.Operator));
        Assert.Null(ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.Screen));
        Assert.Null(ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.Sequence));
        Assert.Null(ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.Recipe));
        Assert.Null(ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.Script));
        Assert.Null(ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.Annunciator));
    }

    [Fact]
    public void ExternalWriteToLiveDevice_IsAllowed()
    {
        var device = new Device { Name = "PLC-1", SimulationEnabled = false };
        Assert.Null(ControlCommandService.SimulationWriteRefusal(device, ControlOrigin.External));
    }

    [Fact]
    public void SimulatedSourceWriteToLiveDevice_IsRefused_ButAllowedOnSimulatedDevice()
    {
        var live = new Device { Name = "PLC-2", SimulationEnabled = false };
        var reason = ControlCommandService.SimulationWriteRefusal(live, ControlOrigin.Script, sourceSimulated: true);
        Assert.NotNull(reason);
        Assert.Contains("源自仿真", reason);
        Assert.Contains("PLC-2", reason);
        Assert.NotNull(ControlCommandService.SimulationWriteRefusal(null, ControlOrigin.Script, sourceSimulated: true));
        var simulated = new Device { Name = "PLC-1", SimulationEnabled = true };
        Assert.Null(ControlCommandService.SimulationWriteRefusal(simulated, ControlOrigin.Script, sourceSimulated: true));
    }
}
