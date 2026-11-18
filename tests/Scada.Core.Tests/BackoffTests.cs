using Scada.Core.Models;

namespace Scada.Core.Tests;

public sealed class BackoffTests
{
    [Fact]
    public void ExponentialDelayUsesJitterAndHonorsTheCap()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.InRange(Backoff.Next(0, 1000, 30_000), 750, 1_250);
            Assert.InRange(Backoff.Next(60_000, 1000, 30_000), 22_500, 30_000);
        }
    }
}