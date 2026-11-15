using Scada.Data;

namespace Scada.Data.Tests;

/// <summary>/metrics 暴露的迁移快照：只记录本次启动，重启后重新记，不累加。</summary>
public sealed class MigrationTelemetryTests
{
    [Fact]
    public void UninitializedHostReportsNoMigrationData()
    {
        var telemetry = new MigrationTelemetry();
        Assert.False(telemetry.Recorded);
        Assert.Equal(0, telemetry.Applied);
        Assert.Equal(0, telemetry.Total);
    }

    [Fact]
    public void RecordPublishesAllFieldsAndOverwritesOnTheNextStart()
    {
        var telemetry = new MigrationTelemetry();
        telemetry.Record(12, 12, 63, 4.25);
        Assert.True(telemetry.Recorded);
        Assert.Equal(12, telemetry.Pending);
        Assert.Equal(12, telemetry.Applied);
        Assert.Equal(63, telemetry.Total);
        Assert.Equal(4.25, telemetry.DurationSeconds, 2);
        Assert.True(telemetry.RecordedAt > DateTimeOffset.UtcNow.AddMinutes(-1));

        // 下一次启动必须覆盖，不能把两次启动的条数累加成 counter。
        telemetry.Record(0, 0, 63, 0.02);
        Assert.Equal(0, telemetry.Applied);
        Assert.Equal(63, telemetry.Total);
    }
}
