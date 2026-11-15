using Scada.Desktop.App;

namespace Scada.Desktop.Tests;

public sealed class RestartPolicyTests
{
    [Fact]
    public void UsesBackoffSequenceAndPauses()
    {
        var policy = new RestartPolicy();
        var now = DateTimeOffset.UtcNow;
        var expected = new[] { 2, 5, 15, 30, 60 };

        foreach (var seconds in expected)
        {
            var delay = policy.RecordCrash(now, TimeSpan.FromMinutes(1));
            Assert.NotNull(delay);
            Assert.Equal(seconds, (int)delay!.Value.TotalSeconds);
        }

        Assert.Null(policy.RecordCrash(now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void StableRunResetsCrashCount()
    {
        var policy = new RestartPolicy();
        var now = DateTimeOffset.UtcNow;
        policy.RecordCrash(now, TimeSpan.FromMinutes(1));
        policy.RecordCrash(now, TimeSpan.FromMinutes(1));

        var delay = policy.RecordCrash(now, TimeSpan.FromMinutes(11));
        Assert.Equal(2, (int)delay!.Value.TotalSeconds);
    }

    [Fact]
    public void OldCrashesAreForgotten()
    {
        var policy = new RestartPolicy();
        var now = DateTimeOffset.UtcNow;
        policy.RecordCrash(now, TimeSpan.FromMinutes(1));

        var delay = policy.RecordCrash(now.AddMinutes(11), TimeSpan.FromMinutes(1));
        Assert.Equal(2, (int)delay!.Value.TotalSeconds);
    }

    [Fact]
    public void ResetClearsHistory()
    {
        var policy = new RestartPolicy();
        var now = DateTimeOffset.UtcNow;
        policy.RecordCrash(now, TimeSpan.FromMinutes(1));
        policy.RecordCrash(now, TimeSpan.FromMinutes(1));
        policy.Reset();

        Assert.Equal(2, (int)policy.RecordCrash(now, TimeSpan.FromMinutes(1))!.Value.TotalSeconds);
    }
}
