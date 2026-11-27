using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Engine.Alarms;

namespace Scada.Engine.Tests;

public class AlarmSinkTests
{
    [Fact]
    public async Task RaiseDeviceOffline_MarksSimulatedWhenDeviceOrReasonIsDrill()
    {
        await using var runtimeConnection = TestRuntime.Open();
        var runtime = TestRuntime.Factory(runtimeConnection);
        var sink = new AlarmSink(runtime, new NullAlarmNotifier(), NullLogger<AlarmSink>.Instance);

        var officialId = await sink.RaiseDeviceOfflineAsync(new Device { Id = 1, Name = "泵A" }, "超时", 1000);
        var simulatedDeviceId = await sink.RaiseDeviceOfflineAsync(new Device { Id = 2, Name = "泵B", SimulationEnabled = true }, "超时", 2000);
        var simulatedReasonId = await sink.RaiseDeviceOfflineAsync(new Device { Id = 3, Name = "泵C" }, "仿真故障：通信中断", 3000);

        await using var db = runtime.CreateDbContext();
        var official = await db.AlarmEvents.FindAsync(officialId);
        var byDevice = await db.AlarmEvents.FindAsync(simulatedDeviceId);
        var byReason = await db.AlarmEvents.FindAsync(simulatedReasonId);
        Assert.NotNull(official); Assert.False(official!.Simulated);
        Assert.Equal(AlarmCategory.Diagnostic, official.Category);
        Assert.Equal(AlarmLevel.Warning, official.Level);
        Assert.NotNull(byDevice); Assert.True(byDevice!.Simulated);
        Assert.Equal(AlarmCategory.Diagnostic, byDevice.Category);
        Assert.NotNull(byReason); Assert.True(byReason!.Simulated);
    }
}
