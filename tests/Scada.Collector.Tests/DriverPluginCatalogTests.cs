using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Collector;
using Scada.Collector.Drivers;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Protocols;

namespace Scada.Collector.Tests;

public class DriverPluginCatalogTests
{
    [Fact]
    public void DefaultSettingsLoadNoPluginsAndKeepIec61850Unavailable()
    {
        var catalog = Catalog(new ScadaSettings());
        Assert.False(catalog.LoadingEnabled);
        Assert.False(catalog.HasPlugin(ProtocolType.Iec61850));
        Assert.False(catalog.IsDriverAvailable(ProtocolType.Iec61850));
        Assert.True(catalog.IsDriverAvailable(ProtocolType.ModbusTcp));
        var enabled = Iec(enabled: true);
        Assert.Equal(Iec61850Spec.NotInstalledMessage, ConfigurationValidator.Device(enabled, drivers: catalog));
        Assert.Null(ConfigurationValidator.Device(Iec(enabled: false), drivers: catalog));
    }

    [Fact]
    public void HostContextDoesNotExposeServiceProvider()
    {
        var context = new DriverHostContext(new PlainCredentials(), new NullDriverLogger());
        Assert.Null(typeof(DriverHostContext).GetProperty("Services"));
        Assert.Null(typeof(DriverHostContext).GetMethod("GetService"));
        Assert.NotNull(context.Credentials);
        Assert.NotNull(context.Logger);
        Assert.NotNull(context.Clock);
    }

    [Fact]
    public void InjectedAvailabilityDoesNotUseProcessWideStaticState()
    {
        var available = new FixedAvailability(ProtocolType.Iec61850);
        var missing = new FixedAvailability();
        var enabled = Iec(enabled: true);
        Assert.Null(ConfigurationValidator.Device(enabled, drivers: available));
        Assert.Equal(Iec61850Spec.NotInstalledMessage, ConfigurationValidator.Device(enabled, drivers: missing));
        Assert.Null(typeof(Iec61850Spec).GetMethod("SetClientAvailable"));
        Assert.Null(typeof(Iec61850Spec).GetProperty("ClientAvailable"));
    }

    [Fact]
    public void RootWithoutAllowlistRejectsUnsignedAssemblies()
    {
        using var plugin = new CompiledPlugin("DenyAll", ProtocolType.Iec61850, "IEC 61850 Test");
        var catalog = Catalog(new ScadaSettings { DriverPluginRoot = plugin.Root });
        Assert.True(catalog.LoadingEnabled);
        Assert.False(catalog.HasPlugin(ProtocolType.Iec61850));
        Assert.False(catalog.IsDriverAvailable(ProtocolType.Iec61850));
        Assert.Contains(catalog.Snapshot([]).Failures, x => x.Reason.Contains("DriverPluginAllowSha256"));
    }

    [Fact]
    public void WrongHashIsRejected()
    {
        using var plugin = new CompiledPlugin("WrongHash", ProtocolType.Iec61850, "IEC 61850 Test");
        var catalog = Catalog(new ScadaSettings
        {
            DriverPluginRoot = plugin.Root,
            DriverPluginAllowSha256 = [new string('A', 64)]
        });
        Assert.False(catalog.HasPlugin(ProtocolType.Iec61850));
        Assert.Contains(catalog.Snapshot([]).Failures, x => x.Reason.Contains(plugin.Sha256) && x.Reason.Contains("DriverPluginAllowSha256"));
    }

    [Fact]
    public void AllowlistedIec61850PluginLoadsWithoutOverrideAndStaysInstanceLocal()
    {
        using var plugin = new CompiledPlugin("IecOk", ProtocolType.Iec61850, "Licensed IEC 61850");
        var allowed = Catalog(new ScadaSettings
        {
            DriverPluginRoot = plugin.Root,
            DriverPluginAllowSha256 = [$"0x{plugin.Sha256.ToLowerInvariant()}"]
        });
        var isolated = Catalog(new ScadaSettings());
        Assert.True(allowed.HasPlugin(ProtocolType.Iec61850));
        Assert.True(allowed.IsDriverAvailable(ProtocolType.Iec61850));
        Assert.Null(ConfigurationValidator.Device(Iec(enabled: true), drivers: allowed));
        Assert.True(allowed.TryCreate(ProtocolType.Iec61850, out var driver, out var name));
        Assert.Equal("Licensed IEC 61850", name);
        Assert.NotNull(driver);
        Assert.False(isolated.IsDriverAvailable(ProtocolType.Iec61850));
        Assert.Equal(Iec61850Spec.NotInstalledMessage, ConfigurationValidator.Device(Iec(enabled: true), drivers: isolated));
    }

    [Fact]
    public void BuiltInOverrideIsRejectedUnlessExplicitlyConfigured()
    {
        using var plugin = new CompiledPlugin("OverrideModbus", ProtocolType.ModbusTcp, "Replacement Modbus");
        var blocked = Catalog(new ScadaSettings
        {
            DriverPluginRoot = plugin.Root,
            DriverPluginAllowSha256 = [plugin.Sha256]
        });
        Assert.False(blocked.HasPlugin(ProtocolType.ModbusTcp));
        Assert.True(blocked.IsDriverAvailable(ProtocolType.ModbusTcp));
        Assert.Contains(blocked.Snapshot([(ProtocolType.ModbusTcp, "ModbusTcp", "built-in")]).Failures,
            x => x.Reason.Contains("DriverPluginOverrideProtocols"));

        var allowed = Catalog(new ScadaSettings
        {
            DriverPluginRoot = plugin.Root,
            DriverPluginAllowSha256 = [plugin.Sha256],
            DriverPluginOverrideProtocols = ["modbustcp"]
        });
        Assert.True(allowed.HasPlugin(ProtocolType.ModbusTcp));
        Assert.True(allowed.TryCreate(ProtocolType.ModbusTcp, out var driver, out var name));
        Assert.Equal("Replacement Modbus", name);
        Assert.NotNull(driver);
        var snapshot = allowed.Snapshot([(ProtocolType.ModbusTcp, "ModbusTcp", "built-in")]);
        var registration = Assert.Single(snapshot.Drivers, x => x.Protocol == ProtocolType.ModbusTcp);
        Assert.True(registration.IsPlugin);
        Assert.True(registration.OverridesBuiltIn);
    }

    private static DriverPluginCatalog Catalog(ScadaSettings settings) =>
        new(Options.Create(settings), new PlainCredentials(), NullLogger<DriverPluginCatalog>.Instance);

    private static Device Iec(bool enabled) => new()
    {
        Name = "ied", Protocol = ProtocolType.Iec61850, Host = "h", Port = 102, Enabled = enabled
    };

    private sealed class FixedAvailability(params ProtocolType[] protocols) : IProtocolDriverAvailability
    {
        public bool IsDriverAvailable(ProtocolType protocol) => protocols.Contains(protocol);
    }

    private sealed class NullDriverLogger : IDriverLogger
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warn(string message, Exception? exception = null) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class CompiledPlugin : IDisposable
    {
        public string Root { get; }
        public string Sha256 { get; }
        private readonly string _workspace;

        public CompiledPlugin(string assemblyName, ProtocolType protocol, string providerName)
        {
            _workspace = Path.Combine(Path.GetTempPath(), "scada-plugin-tests", $"{assemblyName}-{Guid.NewGuid():N}");
            Root = Path.Combine(_workspace, "plugins");
            Directory.CreateDirectory(Path.Combine(_workspace, "src"));
            Directory.CreateDirectory(Root);

            var core = Path.Combine(AppContext.BaseDirectory, "Scada.Core.dll");
            if (!File.Exists(core)) throw new FileNotFoundException("测试宿主未输出 Scada.Core.dll", core);

            File.WriteAllText(Path.Combine(_workspace, "src", "Plugin.cs"),
                $$"""
                using Scada.Core.Entities;
                using Scada.Core.Enums;
                using Scada.Core.Interfaces;
                using Scada.Core.Models;

                public sealed class TestProvider : IDeviceDriverProvider
                {
                    public ProtocolType Protocol => ProtocolType.{{protocol}};
                    public string Name => "{{providerName}}";
                    public string Version => "test";
                    public IDeviceDriver Create(DriverHostContext context) => new TestDriver();
                }

                file sealed class TestDriver : IDeviceDriver
                {
                    public bool IsConnected => false;
                    public string? LastError => null;
                    public Task ConnectAsync(Device device, CancellationToken cancellationToken = default) => Task.CompletedTask;
                    public void Disconnect() { }
                    public Task<IReadOnlyList<RawTagValue>> ReadAsync(Device device, ReadPlan plan, CancellationToken cancellationToken = default) =>
                        Task.FromResult<IReadOnlyList<RawTagValue>>([]);
                    public Task WriteAsync(Device device, Tag tag, double value, CancellationToken cancellationToken = default) => Task.CompletedTask;
                    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
                }
                """);

            File.WriteAllText(Path.Combine(_workspace, "src", $"{assemblyName}.csproj"),
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                    <AssemblyName>{assemblyName}</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Scada.Core">
                      <HintPath>{core}</HintPath>
                      <Private>false</Private>
                    </Reference>
                  </ItemGroup>
                </Project>
                """);

            var start = new ProcessStartInfo("dotnet", "build -c Release -o ../out --nologo")
            {
                WorkingDirectory = Path.Combine(_workspace, "src"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 dotnet build");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"编译测试插件失败（{process.ExitCode}）：\n{stdout}\n{stderr}");

            var built = Path.Combine(_workspace, "out", assemblyName + ".dll");
            var dest = Path.Combine(Root, assemblyName + ".dll");
            File.Copy(built, dest, overwrite: true);
            Sha256 = DriverPluginPolicy.FileSha256(dest);
        }

        public void Dispose()
        {
            try { Directory.Delete(_workspace, recursive: true); } catch { }
        }
    }
}
