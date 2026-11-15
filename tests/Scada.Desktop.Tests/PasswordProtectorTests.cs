using Scada.Desktop.Configuration;

namespace Scada.Desktop.Tests;

public sealed class PasswordProtectorTests
{
    [Fact]
    public void RoundTripsPassword()
    {
        var cipher = PasswordProtector.Protect("p@ss word 中文");
        Assert.NotEmpty(cipher);
        Assert.NotEqual("p@ss word 中文", cipher);
        Assert.Equal("p@ss word 中文", PasswordProtector.Unprotect(cipher));
    }

    [Fact]
    public void EmptyPasswordProducesEmptyCipher()
    {
        Assert.Equal(string.Empty, PasswordProtector.Protect(string.Empty));
        Assert.Null(PasswordProtector.Unprotect(string.Empty));
        Assert.Null(PasswordProtector.Unprotect(null));
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA")]
    public void CorruptCipherReturnsNull(string cipher)
    {
        Assert.Null(PasswordProtector.Unprotect(cipher));
    }
}
