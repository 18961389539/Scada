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

    [Fact]
    public void DesktopClientRangesAndVersionFormatAreValidated()
    {
        var settings = new ScadaSettings
        {
            DesktopClient = new DesktopClientOptions
            {
                HeartbeatSeconds = 1,
                OnlineThresholdSeconds = 5,
                MinimumVersion = "not-a-version",
                LatestVersion = "1.0.0",
                DownloadUrl = "javascript:alert(1)"
            }
        };

        var codes = ScadaSettingsValidation.Validate(settings).Select(x => x.Code).ToArray();

        Assert.Contains("settings.DesktopClient.HeartbeatSeconds", codes);
        Assert.Contains("settings.DesktopClient.OnlineThresholdSeconds", codes);
        Assert.Contains("settings.desktopClient.minimumVersion", codes);
        Assert.Contains("settings.desktopClient.downloadUrl", codes);
    }

    [Fact]
    public void LatestDesktopVersionCannotBeBelowMinimum()
    {
        var settings = new ScadaSettings
        {
            DesktopClient = new DesktopClientOptions { MinimumVersion = "2.0.0", LatestVersion = "1.9.0" }
        };

        Assert.Contains(ScadaSettingsValidation.Validate(settings), x => x.Code == "settings.desktopClient.versionOrder");
    }
    [Fact]
    public void ReverseProxyRequiresTrustedProxies()
    {
        var settings = new ScadaSettings { ReverseProxy = new ReverseProxyOptions { Enabled = true } };
        Assert.Contains(ScadaSettingsValidation.Validate(settings), x => x.Code == "settings.reverseProxy.knownProxies");
    }

    [Fact]
    public void ReverseProxyRejectsUnparsableAddress()
    {
        var settings = new ScadaSettings
        {
            ReverseProxy = new ReverseProxyOptions { Enabled = true, KnownProxies = ["not-an-ip"] }
        };
        Assert.Contains(ScadaSettingsValidation.Validate(settings), x => x.Code == "settings.reverseProxy.knownProxies");
    }

    [Fact]
    public void ReverseProxyAcceptsExplicitAddresses()
    {
        var settings = new ScadaSettings
        {
            ReverseProxy = new ReverseProxyOptions { Enabled = true, KnownProxies = ["10.0.0.5", "127.0.0.1"] }
        };
        Assert.Empty(ScadaSettingsValidation.Validate(settings));
    }
}
