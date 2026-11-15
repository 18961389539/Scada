using System.Reflection;
using System.Security.Cryptography;
using Scada.Core.Entities;
using Scada.Core.Security;

namespace Scada.Core.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void HashAndVerifyRoundTrip()
    {
        var hash = PasswordHasher.Hash("p@ssw0rd");

        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.True(PasswordHasher.Verify("p@ssw0rd", hash));
        Assert.False(PasswordHasher.Verify("p@ssw0rD", hash));
    }

    [Fact]
    public void VerifiesLegacyFormatAndFlagsRehash()
    {
        var legacy = LegacyHash("p@ssw0rd");

        Assert.True(PasswordHasher.Verify("p@ssw0rd", legacy));
        Assert.True(PasswordHasher.NeedsRehash(legacy));
        Assert.False(PasswordHasher.NeedsRehash(PasswordHasher.Hash("p@ssw0rd")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("junk")]
    [InlineData("0.abc.def")]
    [InlineData("100000..AAAA")]
    [InlineData("pbkdf2-sha256$0$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$100000$%%%$%%%")]
    [InlineData("pbkdf2-sha256$100000$AAAA$AAAA")]
    [InlineData("pbkdf2-sha256$999999999$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("argon2id$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void CorruptedHashReturnsFalseInsteadOfThrowing(string stored)
    {
        // 库里一条脏数据不应该把登录接口打成 500。
        Assert.False(PasswordHasher.Verify("p@ssw0rd", stored));
    }

    private static string LegacyHash(string password, int iterations = 100_000)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }
}

public class AuditSensitivityTests
{
    public sealed class SampleSecretEntity
    {
        public string Name { get; set; } = "";
        [Sensitive] public string ApiToken { get; set; } = "";
    }

    [Fact]
    public void SensitiveAttributeRedactsValueAndKeepsChangeMarker()
    {
        var before = new SampleSecretEntity { Name = "a", ApiToken = "old-token" };
        var after = new SampleSecretEntity { Name = "b", ApiToken = "new-token" };

        var json = AuditDiff.Compute(before, after);

        Assert.DoesNotContain("old-token", json);
        Assert.DoesNotContain("new-token", json);
        Assert.Contains("***", json);
        Assert.Contains("\"name\"", json);
    }

    [Fact]
    public void UserPasswordHashIsRedacted()
    {
        var json = AuditDiff.Compute(new User { UserName = "u", PasswordHash = "hash-1" }, new User { UserName = "u", PasswordHash = "hash-2" });

        Assert.DoesNotContain("hash-1", json);
        Assert.DoesNotContain("hash-2", json);
    }

    [Fact]
    public void EverySecretNamedPropertyIsMarkedSensitive()
    {
        // fail-closed：名字像密钥的字段必须显式标注 [Sensitive]，否则新加的 ApiKey 会默认进审计日志。
        var unmarked = typeof(User).Assembly.GetTypes()
            .Where(type => type.Namespace?.StartsWith("Scada.Core", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(string)
                    && LooksSecret(property.Name)
                    && !property.IsDefined(typeof(SensitiveAttribute), inherit: true))
                .Select(property => $"{type.Name}.{property.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unmarked);
    }

    private static bool LooksSecret(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("Token", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("TokenHash", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("KeyHash", StringComparison.OrdinalIgnoreCase)
        || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase);
}
