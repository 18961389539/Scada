using Scada.Desktop.Configuration;

namespace Scada.Desktop.Tests;

public sealed class SettingsValidatorTests
{
    [Theory]
    [InlineData("192.168.1.10:5080", "http://192.168.1.10:5080")]
    [InlineData("http://localhost:5080/", "http://localhost:5080")]
    [InlineData("https://scada.example.com", "https://scada.example.com")]
    public void NormalizesServerUrl(string input, string expected)
    {
        Assert.True(SettingsValidator.TryNormalizeServerUrl(input, out var normalized, out var error));
        Assert.Equal(expected, normalized);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://localhost:5080")]
    [InlineData("http://host:5080/subpath")]
    [InlineData("http://user:pass@host:5080")]
    [InlineData("http://host:5080/?a=1")]
    [InlineData("http://host:5080/#x")]
    public void RejectsInvalidServerUrl(string input)
    {
        Assert.False(SettingsValidator.TryNormalizeServerUrl(input, out _, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RejectsScreenTargetWithoutId()
    {
        var settings = ValidSettings();
        settings.Target = new DesktopTarget { Kind = "screen" };
        Assert.Contains("画面", SettingsValidator.Validate(settings, requirePassword: false));
    }

    [Fact]
    public void RejectsLayoutTargetWithoutId()
    {
        var settings = ValidSettings();
        settings.Target = new DesktopTarget { Kind = "layout" };
        Assert.Contains("布局", SettingsValidator.Validate(settings, requirePassword: false));
    }

    [Fact]
    public void RejectsWorkstationTargetWithoutId()
    {
        var settings = ValidSettings();
        settings.Target = new DesktopTarget { Kind = "workstation" };
        Assert.Contains("工作站", SettingsValidator.Validate(settings, requirePassword: false));
    }

    [Fact]
    public void RequiresPasswordWhenSavingIsEnabled()
    {
        var settings = ValidSettings();
        settings.SavePassword = true;
        settings.PasswordCipher = null;
        Assert.NotEmpty(SettingsValidator.Validate(settings, requirePassword: true)!);
    }

    [Fact]
    public void AllowsMissingPasswordWhenSavingIsDisabled()
    {
        var settings = ValidSettings();
        settings.SavePassword = false;
        settings.PasswordCipher = null;
        Assert.Null(SettingsValidator.Validate(settings, requirePassword: true));
    }

    [Fact]
    public void RejectsNewerConfigVersion()
    {
        var settings = ValidSettings();
        settings.Version = 2;
        Assert.Contains("版本", SettingsValidator.Validate(settings, requirePassword: false));
    }

    private static DesktopSettings ValidSettings() => new()
    {
        ServerUrl = "http://localhost:5080",
        Target = new DesktopTarget { Kind = "screen", ScreenId = 1 },
        SavePassword = false
    };
}
