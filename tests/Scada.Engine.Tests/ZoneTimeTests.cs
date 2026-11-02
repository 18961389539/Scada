using Scada.Engine.Calculations;

namespace Scada.Engine.Tests;

public class ZoneTimeTests
{
    [Fact]
    public void HalfHourOffsetHoursStartOnTheLocalClockHour()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("Test-IST", TimeSpan.FromMinutes(330), "IST", "IST");
        var midnight = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromMinutes(330)).ToUnixTimeMilliseconds();
        var quarterPast = midnight + 15 * 60_000;
        Assert.Equal(midnight, ZoneTime.FloorHour(quarterPast, india));
        Assert.NotEqual(quarterPast / 3_600_000L * 3_600_000L, ZoneTime.FloorHour(quarterPast, india));

        var china = TimeZoneInfo.CreateCustomTimeZone("Test-CST", TimeSpan.FromHours(8), "CST", "CST");
        var ts = new DateTimeOffset(2026, 2, 1, 10, 20, 0, TimeSpan.FromHours(8)).ToUnixTimeMilliseconds();
        Assert.Equal(ts / 3_600_000L * 3_600_000L, ZoneTime.FloorHour(ts, china));
    }
}
