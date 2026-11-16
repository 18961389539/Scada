using System.IO.Compression;
using System.Security.Cryptography;
using Scada.Engine.Maintenance;

namespace Scada.Engine.Tests;

public sealed class BackupReencryptTests
{
    private const string OldPassword = "old-password-123";
    private const string NewPassword = "new-password-456";

    private static async Task<(TempDir Dir, string Source)> EncryptedPackage(string content = "scada")
    {
        var dir = new TempDir("scada-reencrypt-");
        var plain = Path.Combine(dir.Path, "input.zip");
        using (var zip = ZipFile.Open(plain, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("manifest.json");
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(content);
        }

        var source = Path.Combine(dir.Path, "full_20260101_000000_source.zip.enc");
        await BackupArchive.EncryptAsync(plain, source, OldPassword, default);
        File.Delete(plain);
        return (dir, source);
    }

    [Fact]
    public async Task RotationKeepsTheOldPackageAndProducesAnIdenticalNewOne()
    {
        var (dir, source) = await EncryptedPackage();
        using var _ = dir;
        var before = File.ReadAllBytes(source);
        var target = Path.Combine(dir.Path, "full_20260101_000000_rotated.zip.enc");

        var result = await BackupReencrypt.RunAsync(source, target, OldPassword, NewPassword);

        Assert.Equal(source, result.SourcePath);
        Assert.Equal(target, result.TargetPath);
        Assert.True(File.Exists(target));
        Assert.Equal(before, File.ReadAllBytes(source));
        var plain = Path.Combine(dir.Path, "decrypted.zip");
        await BackupArchive.DecryptAsync(target, plain, NewPassword, default);
        using var archive = ZipFile.OpenRead(plain);
        using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
        Assert.Equal("scada", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task AWrongOldPasswordLeavesNoTargetBehind()
    {
        var (dir, source) = await EncryptedPackage();
        using var _ = dir;
        var target = Path.Combine(dir.Path, "full_20260101_000000_rotated.zip.enc");

        await Assert.ThrowsAsync<BackupArchiveException>(() => BackupReencrypt.RunAsync(source, target, "wrong-password", NewPassword));

        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(dir.Path, ".reenc-*"));
    }

    [Fact]
    public async Task RotationRefusesToOverwriteOrReuseAPassword()
    {
        var (dir, source) = await EncryptedPackage();
        using var _ = dir;
        var target = Path.Combine(dir.Path, "full_20260101_000000_rotated.zip.enc");

        await Assert.ThrowsAsync<ArgumentException>(() => BackupReencrypt.RunAsync(source, source, OldPassword, NewPassword));
        await Assert.ThrowsAsync<ArgumentException>(() => BackupReencrypt.RunAsync(source, target, OldPassword, OldPassword));

        File.WriteAllText(target, "occupied");
        await Assert.ThrowsAsync<IOException>(() => BackupReencrypt.RunAsync(source, target, OldPassword, NewPassword));
        Assert.Equal("occupied", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RotationRejectsPlainPackagesAndForeignDirectories()
    {
        var (dir, source) = await EncryptedPackage();
        using var _ = dir;
        var plain = Path.Combine(dir.Path, "full_20260101_000000_plain.zip");
        using (var zip = ZipFile.Open(plain, ZipArchiveMode.Create)) zip.CreateEntry("manifest.json");
        var target = Path.Combine(dir.Path, "full_20260101_000000_rotated.zip.enc");

        await Assert.ThrowsAsync<ArgumentException>(() => BackupReencrypt.RunAsync(plain, target, OldPassword, NewPassword));

        var other = new TempDir("scada-reencrypt-other-");
        using var __ = other;
        var foreign = Path.Combine(other.Path, "full_20260101_000000_rotated.zip.enc");
        await Assert.ThrowsAsync<ArgumentException>(() => BackupReencrypt.RunAsync(source, foreign, OldPassword, NewPassword));
        Assert.False(File.Exists(foreign));
    }

    [Fact]
    public async Task RotationRejectsATargetOutsideTheBackupNamingScheme()
    {
        var (dir, source) = await EncryptedPackage();
        using var _ = dir;
        var target = Path.Combine(dir.Path, "archive.zip.enc");

        await Assert.ThrowsAsync<ArgumentException>(() => BackupReencrypt.RunAsync(source, target, OldPassword, NewPassword));
        Assert.False(File.Exists(target));
    }
}