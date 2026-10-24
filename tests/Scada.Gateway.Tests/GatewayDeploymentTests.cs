using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Scada.Gateway.Hosting;

namespace Scada.Gateway.Tests;

public sealed class GatewayDeploymentTests
{
    [Fact]
    public void ValidationAcceptsTheEqualsSyntax() => Assert.True(GatewayAgentConfiguration.ValidationRequested(["--ValidateConfig=true"]));
    [Fact]
    public void ValidationAcceptsSeparateArguments() => Assert.True(GatewayAgentConfiguration.ValidationRequested(["--ValidateConfig", "true"]));
    [Fact]
    public void ExplicitFalseIsANormalStart() => Assert.False(GatewayAgentConfiguration.ValidationRequested(["--ValidateConfig=false"]));
    [Fact]
    public void NoValidationArgumentIsANormalStart() => Assert.False(GatewayAgentConfiguration.ValidationRequested([]));
    [Fact]
    public void AValidationArgumentWithoutAValueCannotStartAcquisition() => Assert.Throws<ArgumentException>(() => GatewayAgentConfiguration.ValidationRequested(["--ValidateConfig"]));
    [Fact]
    public void AnInvalidValidationValueCannotStartAcquisition() => Assert.Throws<InvalidOperationException>(() => GatewayAgentConfiguration.ValidationRequested(["--ValidateConfig=wrong"]));

    [Fact]
    public void LegacyConfigurationRemainsCompatible()
    {
        using var config = new ConfigurationManager(); config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GatewayAgent:ServerUrl"] = "https://center.example/", ["GatewayAgent:GatewayId"] = "12",
            ["GatewayAgent:Token"] = new string('t', 43)
        });
        var options = GatewayAgentConfiguration.Read(config);
        Assert.Equal(12, options.GatewayId); Assert.Equal("ScadaGateway", options.ServiceName);
    }

    [Fact]
    public async Task ServiceFileIsAuthoritativeOverAmbientIdentityAndRuntimeSettings()
    {
        using var dir = new GatewayAgentStoreTests.StoreDirectory();
        var path = Path.Combine(dir.Root, "service configuration.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { GatewayAgent = new
        {
            ServerUrl = "https://installed.example/", GatewayId = 2, Token = new string('k', 43),
            DataRoot = Path.Combine(dir.Root, "data"), ServiceName = "ScadaGateway_line_2"
        } }));
        using var config = new ConfigurationManager(); config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GatewayConfig"] = path, ["GatewayAgent:ServerUrl"] = "https://wrong.example/",
            ["GatewayAgent:GatewayId"] = "99", ["GatewayAgent:Token"] = new string('x', 43),
            ["GatewayAgent:DataRoot"] = "wrong-data", ["GatewayAgent:HeartbeatIntervalMs"] = "1"
        });
        var options = GatewayAgentConfiguration.Read(config);
        Assert.Equal(2, options.GatewayId); Assert.Equal("https://installed.example/", options.ServerUrl);
        Assert.Equal(new string('k', 43), options.Token); Assert.Equal("ScadaGateway_line_2", options.ServiceName);
        Assert.Equal(Path.Combine(dir.Root, "data"), options.DataRoot); Assert.Equal(5000, options.HeartbeatIntervalMs);
    }

    [Theory]
    [InlineData("relative.json")]
    [InlineData("")]
    public void ServiceFileMustHaveAnAbsolutePath(string path)
    {
        using var config = new ConfigurationManager(); config.AddInMemoryCollection(new Dictionary<string, string?> { ["GatewayConfig"] = path });
        Assert.Throws<ArgumentException>(() => GatewayAgentConfiguration.Read(config));
    }

    [Fact]
    public async Task MissingOrOversizedServiceFileNeverFallsBackToAmbientCredentials()
    {
        using var dir = new GatewayAgentStoreTests.StoreDirectory(); var path = Path.Combine(dir.Root, "service.json");
        using var config = new ConfigurationManager(); config.AddInMemoryCollection(new Dictionary<string, string?> { ["GatewayConfig"] = path });
        Assert.Throws<FileNotFoundException>(() => GatewayAgentConfiguration.Read(config));
        await File.WriteAllTextAsync(path, new string(' ', 64 * 1024 + 1));
        Assert.Throws<InvalidDataException>(() => GatewayAgentConfiguration.Read(config));
    }

    [Theory]
    [InlineData("Bad Service")]
    [InlineData("123")]
    [InlineData("../unsafe")]
    [InlineData("")]
    public void InvalidServiceNamesAreRejectedBeforeStarting(string name)
    {
        var options = new GatewayAgentOptions { ServerUrl = "https://center.example/", GatewayId = 1, Token = new string('t', 43), ServiceName = name };
        Assert.Throws<ArgumentException>(options.Validate);
    }
}
