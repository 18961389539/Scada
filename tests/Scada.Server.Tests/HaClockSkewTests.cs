using Scada.Engine.Maintenance;
using Scada.Server.Ha;

namespace Scada.Server.Tests;

public sealed class HaClockSkewTests
{
    [Fact]
    public void HeartbeatClockSkewUsesTripleClockSkewMsAndDoesNotWarnWithinLimit()
    {
        var peer = new HaPeerStatus("peer", "Primary", 1, "http://peer", Ts: 10_000);
        Assert.Null(HaCoordinator.PeerClockSkewWarning(peer, localTs: 10_000 + 2_999, clockSkewMs: 1000));
        var warning = HaCoordinator.PeerClockSkewWarning(peer, localTs: 10_000 + 3_001, clockSkewMs: 1000);
        Assert.NotNull(warning);
        Assert.Equal("ha_clock_skew", warning.Code);
        Assert.Contains("不会自动修改系统时钟", warning.Message);
    }
}