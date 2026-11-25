using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Engine.Maintenance;
namespace Scada.Server.Tests;

public sealed class BackupSecurityTests
{
    [Theory]
    [InlineData(false, null, 1)]
    [InlineData(true, null, 0)]
    [InlineData(false, "test-password-only", 0)]
    public void ProductionWarningIsActionableWithoutSecret(bool development, string? password, int count)
    {
        var logger = new CapturingLogger();
        BackupSecurity.WarnIfUnencrypted(development, new() { BackupPassword = password }, logger);
        Assert.Equal(count, logger.Messages.Count);
        if (count > 0) Assert.Contains("Scada:BackupPassword", logger.Messages[0]);
        Assert.DoesNotContain(logger.Messages, x => x.Contains("test-password-only"));
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(false, "test-password-only", false)]
    public void UnencryptedWarningIsActionableWithoutSecret(bool development, string? password, bool expected)
    {
        var warning = BackupSecurity.UnencryptedWarning(development, new() { BackupPassword = password });
        Assert.Equal(expected, warning is not null);
        if (expected)
        {
            Assert.Equal(BackupSecurity.UnencryptedWarningCode, warning!.Code);
            Assert.Contains("Set-BackupPassword.ps1", warning.Message);
            Assert.Contains("Scada:BackupPassword", warning.Message);
            Assert.DoesNotContain("test-password-only", warning.Message);
        }
    }

    [Fact]
    public async Task PlaintextDoesNotConstructTargetOrDeleteOldRemoteBackups()
    {
        using var rig = new BackupServiceTests.Rig();
        var remote = Path.Combine(rig.Root, "must-not-create");
        await rig.PrepareAsync(s => { s.BackupDrillEnabled = false; s.BackupTargets = [new() { Type = "Directory", Path = remote }]; });
        var constructed = false;
        var scheduler = new BackupScheduler(rig.Backups, Options.Create(rig.Settings), NullLogger<BackupScheduler>.Instance,
            targetFactory: _ => { constructed = true; throw new Exception("must not run"); });
        var run = await scheduler.RunAsync("test", default);
        Assert.False(run.Ok);
        Assert.False(constructed);
        Assert.Contains("NEW encrypted backup", Assert.Single(run.Targets).Error);
        Assert.False(Directory.Exists(remote));
        Assert.Single(rig.Backups.List());
    }

    [Fact]
    public async Task RenamingPlaintextToEncDoesNotBypassDirectoryOrS3Guard()
    {
        using var rig = new BackupServiceTests.Rig();
        Directory.CreateDirectory(rig.Root);
        var path = Path.Combine(rig.Root, "fake.zip.enc");
        await File.WriteAllTextAsync(path, "PK this is plaintext");
        var directory = new DirectoryBackupTarget(new() { Path = Path.Combine(rig.Root, "offsite") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => directory.UploadAsync(path, "fake.zip.enc", default));
        using var s3 = new S3BackupTarget(new() { Endpoint = "https://example.invalid", Bucket = "test", AccessKey = "test", SecretKey = "test" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => s3.UploadAsync(path, "fake.zip.enc", default));
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
