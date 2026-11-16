using Scada.Core.Security;

namespace Scada.Core.Tests;

public sealed class AuditQueryTests
{
    [Theory]
    [InlineData("token")]
    [InlineData("access_token")]
    [InlineData("apiKey")]
    [InlineData("api%5Fkey")]
    [InlineData("secret")]
    [InlineData("password")]
    [InlineData("X-Signature")]
    [InlineData("session")]
    [InlineData("code")]
    public void SensitiveParameterNamesAreDetected(string name) => Assert.True(AuditQuery.IsSensitiveName(name));

    [Theory]
    [InlineData("tagId")]
    [InlineData("page")]
    [InlineData("from")]
    public void OrdinaryParameterNamesAreNotSensitive(string name) => Assert.False(AuditQuery.IsSensitiveName(name));

    [Fact]
    public void SensitiveValuesAreRedactedButOtherParametersRemainReadable()
    {
        var scrubbed = AuditQuery.Scrub("?tagId=17&token=scada_abcdef&page=2&apiKey=deadbeef");
        Assert.Equal("tagId=17&token=***&page=2&apiKey=***", scrubbed);
        Assert.DoesNotContain("scada_abcdef", scrubbed);
        Assert.DoesNotContain("deadbeef", scrubbed);
    }

    [Fact]
    public void ValuesWithoutEqualsAndEmptyQueriesAreHandled()
    {
        Assert.Equal(string.Empty, AuditQuery.Scrub(null));
        Assert.Equal(string.Empty, AuditQuery.Scrub("?"));
        Assert.Equal("flag&token=***", AuditQuery.Scrub("flag&token=abc"));
    }

    [Fact]
    public void LongQueryStringCannotGrowTheAuditRowWithoutBound()
    {
        // 用非敏感参数触发截断：敏感值会先被换成 ***，本身就不会撑大审计行。
        var scrubbed = AuditQuery.Scrub("?token=scada_abcdef&note=" + new string('x', 100_000), maxLength: 128);
        Assert.Equal(129, scrubbed.Length);
        Assert.EndsWith("…", scrubbed);
        Assert.StartsWith("token=***&note=", scrubbed);
        Assert.DoesNotContain("scada_abcdef", scrubbed);
    }

    [Fact]
    public void TooManyParametersAreCapped()
    {
        var query = "?" + string.Join("&", Enumerable.Range(0, 500).Select(i => $"p{i}={i}"));
        // 64 段上限，避免攻击者用几万个参数把每次审计都撑成一行大 JSON。
        Assert.Equal(64, AuditQuery.Scrub(query, maxLength: 100_000).Split('&').Length);
    }
}
