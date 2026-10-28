using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Licensing;
using Scada.Core.Options;
using Scada.Server.Licensing;

namespace Scada.Server.Tests;

public sealed class LicenseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "scada-license-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _publicKeyPem;

    public LicenseTests()
    {
        Directory.CreateDirectory(_root);
        _publicKeyPem = _signingKey.ExportSubjectPublicKeyInfoPem();
    }

    public void Dispose()
    {
        _signingKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Token_RoundTrips_Payload()
    {
        var payload = Payload(expiresInDays: 30, machineId: "ABCDE-FGHIJ");
        var code = LicenseToken.Create(payload, _signingKey);

        var parsed = LicenseToken.Parse(code, _signingKey);

        Assert.True(parsed.Valid);
        Assert.Equal(payload.LicenseId, parsed.Payload!.LicenseId);
        Assert.Equal(payload.Customer, parsed.Payload.Customer);
        Assert.Equal(payload.MachineId, parsed.Payload.MachineId);
        Assert.Equal(payload.MaxDevices, parsed.Payload.MaxDevices);
    }

    [Fact]
    public void Token_Rejects_Tampered_Payload()
    {
        var code = LicenseToken.Create(Payload(expiresInDays: 30, machineId: "ABCDE-FGHIJ"), _signingKey);
        var parts = code.Split('.');
        var tampered = parts[1][..^2] + (parts[1][^2] == 'A' ? "BB" : "AA");
        var modified = $"{parts[0]}.{tampered}.{parts[2]}";

        var parsed = LicenseToken.Parse(modified, _signingKey);

        Assert.False(parsed.Valid);
        Assert.Contains("签名", parsed.Error);
    }

    [Fact]
    public void Token_Rejects_Code_Signed_By_Another_Key()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var code = LicenseToken.Create(Payload(expiresInDays: 30, machineId: "ABCDE-FGHIJ"), other);

        Assert.False(LicenseToken.Parse(code, _signingKey).Valid);
    }

    [Fact]
    public void Service_Starts_In_Trial_For_120_Days()
    {
        var service = Create(new MutableTimeProvider(DateTimeOffset.UtcNow));

        Assert.Equal(LicenseStatus.Trial, service.Current.Status);
        Assert.Equal(120, service.Current.TrialDays);
        Assert.False(service.Current.Blocked);
        Assert.InRange(service.Current.DaysRemaining, 119, 120);
    }

    [Fact]
    public void Service_Expires_After_Trial()
    {
        var start = DateTimeOffset.UtcNow;
        _ = Create(new MutableTimeProvider(start));

        var later = Create(new MutableTimeProvider(start.AddDays(121)));

        Assert.Equal(LicenseStatus.Expired, later.Current.Status);
        Assert.True(later.Current.Blocked);
    }

    [Fact]
    public void Service_Detects_Clock_Rollback()
    {
        var start = DateTimeOffset.UtcNow;
        _ = Create(new MutableTimeProvider(start));

        var rolledBack = Create(new MutableTimeProvider(start.AddHours(-2)));

        Assert.Equal(LicenseStatus.Tampered, rolledBack.Current.Status);
        Assert.True(rolledBack.Current.Blocked);
    }

    [Fact]
    public void Service_Activates_Valid_License()
    {
        var service = Create(new MutableTimeProvider(DateTimeOffset.UtcNow));
        var code = LicenseToken.Create(Payload(expiresInDays: 10, machineId: service.Current.MachineId), _signingKey);

        var (ok, error) = service.Activate(code);

        Assert.True(ok, error);
        Assert.Equal(LicenseStatus.ExpiringSoon, service.Current.Status);
        Assert.Equal("SC-TEST", service.Current.LicenseId);
        Assert.InRange(service.Current.DaysRemaining, 9, 10);
        Assert.False(service.Current.Blocked);
    }

    [Fact]
    public void Service_Rejects_Activation_For_Other_Machine()
    {
        var service = Create(new MutableTimeProvider(DateTimeOffset.UtcNow));
        var code = LicenseToken.Create(Payload(expiresInDays: 30, machineId: "OTHER-MACHINE"), _signingKey);

        var (ok, error) = service.Activate(code);

        Assert.False(ok);
        Assert.Contains("不属于本机", error);
        Assert.Equal(LicenseStatus.Trial, service.Current.Status);
    }

    [Fact]
    public void Service_Rejects_Expired_Activation()
    {
        var service = Create(new MutableTimeProvider(DateTimeOffset.UtcNow));
        var code = LicenseToken.Create(Payload(expiresInDays: -1, machineId: service.Current.MachineId), _signingKey);

        var (ok, error) = service.Activate(code);

        Assert.False(ok);
        Assert.Contains("已过期", error);
    }

    [Fact]
    public void Service_Reports_Device_And_Tag_Limits()
    {
        var service = Create(new MutableTimeProvider(DateTimeOffset.UtcNow));
        var payload = Payload(expiresInDays: 30, machineId: service.Current.MachineId) with { MaxDevices = 50, MaxTags = 800 };
        var code = LicenseToken.Create(payload, _signingKey);

        Assert.True(service.Activate(code).Ok);
        Assert.Equal(50, service.Current.MaxDevices);
        Assert.Equal(800, service.Current.MaxTags);
        Assert.Contains(LicenseFeatures.Ha, service.Current.Features);
    }

    private LicenseService Create(TimeProvider time) =>
        new(
            Options.Create(new ScadaSettings { ConfigDbPath = Path.Combine(_root, "config.db") }),
            Options.Create(new LicenseOptions { PublicKey = _publicKeyPem }),
            NullLogger<LicenseService>.Instance,
            time);

    private static LicensePayload Payload(int expiresInDays, string machineId) => new()
    {
        KeyId = "test",
        LicenseId = "SC-TEST",
        Customer = "测试客户",
        MachineId = machineId,
        IssuedAt = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(expiresInDays).ToUnixTimeSeconds(),
        MaxDevices = 100,
        MaxTags = 1000,
        Features = [LicenseFeatures.Ha]
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}