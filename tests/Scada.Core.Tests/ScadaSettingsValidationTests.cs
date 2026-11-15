using Scada.Core.Options;

namespace Scada.Core.Tests;

public class ScadaSettingsValidationTests
{
    [Fact]
    public void DefaultsAreValid()
    {
        Assert.Empty(ScadaSettingsValidation.Validate(new ScadaSettings()));
    }

    [Fact]
    public void ZeroConcurrencyAndNegativeRetentionAreReportedTogether()
    {
        var settings = new ScadaSettings
        {
            HistoryQueryMaxConcurrent = 0,
            HistoryRetentionMonths = -1,
            PushIntervalMs = 0,
            ConfigRefreshSeconds = 0
        };

        var codes = ScadaSettingsValidation.Validate(settings).Select(x => x.Code).ToArray();

        Assert.Contains("settings.HistoryQueryMaxConcurrent", codes);
        Assert.Contains("settings.HistoryRetentionMonths", codes);
        Assert.Contains("settings.PushIntervalMs", codes);
        Assert.Contains("settings.ConfigRefreshSeconds", codes);
    }

    [Fact]
    public void AnalysisBudgetCannotExceedQueryBudget()
    {
        var settings = new ScadaSettings { HistoryQueryMaxReadPoints = 1000, HistoryAnalysisMaxPoints = 2000 };

        var issue = Assert.Single(ScadaSettingsValidation.Validate(settings));

        Assert.Equal(nameof(ScadaSettings.HistoryAnalysisMaxPoints), issue.Field);
    }

    [Fact]
    public void SpoolCapacityMustFitAtLeastOneSegment()
    {
        var settings = new ScadaSettings { HistorySpoolMaxBytes = 1024, HistorySpoolSegmentBytes = 4096 };

        Assert.Contains(ScadaSettingsValidation.Validate(settings), x => x.Code == "settings.HistorySpoolMaxBytes");
    }

    [Theory]
    [InlineData("Directory", null, "Endpoint", "Bucket")]
    [InlineData("S3", "D:\\backups", null, "bucket")]
    [InlineData("Ftp", null, null, null)]
    public void BackupTargetsRequireTheirEndpoint(string type, string? path, string? endpoint, string? bucket)
    {
        var settings = new ScadaSettings
        {
            BackupTargets = [new BackupTargetOptions { Name = "t", Type = type, Path = path, Endpoint = endpoint, Bucket = bucket }]
        };

        var issue = Assert.Single(ScadaSettingsValidation.Validate(settings));

        Assert.StartsWith("settings.backup", issue.Code);
    }

    [Fact]
    public void UnknownShiftTimeZoneFailsFast()
    {
        var settings = new ScadaSettings { ShiftTimeZone = "Mars/Olympus" };

        Assert.Contains(ScadaSettingsValidation.Validate(settings), x => x.Code == "settings.shiftTimeZone");
    }

    [Fact]
    public void NotificationRetryCeilingCannotBeBelowBase()
    {
        var settings = new ScadaSettings { NotificationRetryBaseSec = 60, NotificationRetryMaxSec = 30 };

        Assert.Contains(ScadaSettingsValidation.Validate(settings), x => x.Code == "settings.NotificationRetryMaxSec");
    }
}
