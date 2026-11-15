using Scada.Core.Licensing;

namespace Scada.Core.Tests;

public class LicenseEvaluationTests
{
    private static LicensePayload Payload(string machineId = "", long expiresAt = 2_000_000_000) =>
        new() { LicenseId = "L1", MachineId = machineId, ExpiresAt = expiresAt };

    [Theory]
    [InlineData("", "ABC-123", true)]
    [InlineData("ABC-123", "ABC123", true)]
    [InlineData("abc-123", "ABC 123", true)]
    [InlineData("ABC-123", "ABC-124", false)]
    public void MachineBindingIgnoresSeparatorsAndCase(string licensed, string actual, bool expected)
    {
        Assert.Equal(expected, LicenseEvaluation.MatchesMachine(Payload(licensed), actual));
    }

    [Fact]
    public void ExpiryIsInclusive()
    {
        var payload = Payload(expiresAt: 1_000);

        Assert.False(LicenseEvaluation.IsExpired(payload, 999));
        Assert.True(LicenseEvaluation.IsExpired(payload, 1_000));
        Assert.True(LicenseEvaluation.IsExpired(payload, 1_001));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(86_400, 1)]
    [InlineData(86_401, 2)]
    [InlineData(-500, 0)]
    public void DaysRemainingRoundsUpAndNeverGoesNegative(long deltaSeconds, int expected)
    {
        Assert.Equal(expected, LicenseEvaluation.DaysRemaining(1_000_000 + deltaSeconds, 1_000_000));
    }

    [Theory]
    [InlineData(1_000, 1_000, 300, false)]
    [InlineData(700, 1_000, 300, false)]
    [InlineData(699, 1_000, 300, true)]
    [InlineData(0, 0, 300, false)]
    [InlineData(1_000, 1_000, 0, false)]
    [InlineData(999, 1_000, 0, true)]
    public void ClockRollbackHonoursSkew(long now, long lastSeen, int skew, bool expected)
    {
        Assert.Equal(expected, LicenseEvaluation.IsClockRollback(now, lastSeen, skew));
    }

    [Theory]
    [InlineData(30, 15, false)]
    [InlineData(15, 15, true)]
    [InlineData(0, 0, true)]
    public void ExpiringSoonUsesAtLeastOneDayWindow(int days, int warningDays, bool expected)
    {
        Assert.Equal(expected, LicenseEvaluation.IsExpiringSoon(days, warningDays));
    }
}
