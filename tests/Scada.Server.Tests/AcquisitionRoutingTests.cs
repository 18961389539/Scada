using System.Text.RegularExpressions;
using Scada.Core.Acquisition;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Server.Services;
using Microsoft.Extensions.Options;
using Scada.Core.Options;

namespace Scada.Server.Tests;

public sealed class AcquisitionRoutingTests
{
    private static readonly string[] RoutingFiles =
    [
        "Services/GatewayCollectorCoordinator.cs",
        "Services/GatewayCenterCommands.cs",
        "Services/GatewayCenterEngineering.cs",
        "Endpoints/CollectorDiagnosticEndpoints.cs",
        "Mcp/McpControlTools.cs",
        "Mcp/McpAnalysisTools.cs",
        "Services/OpcCatalogService.cs",
        "Endpoints/OpcEndpoints.cs",
    ];

    [Fact]
    public void LiveRoutingFilesDoNotBranchOnConfiguredDeviceGatewayId()
    {
        var root = FindRoot();
        var leftover = new List<string>();
        foreach (var file in RoutingFiles)
        {
            var path = Path.Combine(root, "src", "Scada.Server", file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"missing source {file}");
            var text = File.ReadAllText(path);
            Assert.True(text.Contains("Resolve(", StringComparison.Ordinal) || text.Contains("ActiveGatewayId(", StringComparison.Ordinal),
                $"{file} should route with Resolve()/ActiveGatewayId()");
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (line.Contains("UnsupportedMessage(", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(line, @"device\??\.GatewayId"))
                    leftover.Add($"{file}:{i + 1}: {line.Trim()}");
            }
        }
        Assert.True(leftover.Count == 0,
            "现场路由仍按配置 Device.GatewayId 分支，应改为 Resolve()/ActiveGatewayId()：\n" + string.Join("\n", leftover));
    }

    [Fact]
    public async Task TextWriteFollowsResolvedGatewayNotConfiguredPrimary()
    {
        var device = new Device
        {
            Id = 4, Name = "泵", GatewayId = 1, Host = "10.0.0.1", Port = 502,
            StandbyHost = "10.0.0.8", StandbyPort = 502, Enabled = true,
            FailoverHoldMs = 10_000, SwitchbackHoldMs = 30_000
        };
        var tag = new Tag { Id = 9, DeviceId = 4, Name = "text", DataType = Scada.Core.Enums.TagDataType.String, TextLength = 16 };
        var snapshot = new Snapshot(device, tag);
        var paths = new FixedPaths { Path = AcquisitionPathKind.Standby };
        var local = new RecordingLocal();
        await using var connection = TestRuntime.Open();
        var coordinator = new GatewayCollectorCoordinator(local, null!, snapshot, new LocalCommandLedger(TestRuntime.Factory(connection)), Options.Create(new ScadaSettings { LocalCollectionEnabled = true }), paths);
        var result = await coordinator.WriteTextIdentifiedAsync(4, tag, "abc", new CollectorCommandContext(Guid.NewGuid(), 1, "op"), default);
        Assert.True(local.TextWrites == 1, "备路径在本机时，字符串写值应走本机而不是配置主网关");
        Assert.True(result.Success);
    }

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(source) ?? "."); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Scada.sln"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    private sealed class Snapshot(Device device, Tag tag) : IConfigSnapshot
    {
        public IReadOnlyList<Device> Devices { get; } = [device];
        public IReadOnlyList<Tag> Tags { get; } = [tag];
        public IReadOnlyList<Scada.Core.Entities.AlarmRule> AlarmRules { get; } = [];
        public int Version => 1;
        public long LoadedAt => 0;
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool TryGetTag(int tagId, out Tag found) { found = Tags.FirstOrDefault(x => x.Id == tagId)!; return found is not null; }
        public bool TryGetDevice(int deviceId, out Device found) { found = Devices.FirstOrDefault(x => x.Id == deviceId)!; return found is not null; }
    }

    private sealed class FixedPaths : IDeviceAcquisitionPaths
    {
        public AcquisitionPathKind Path { get; set; }
        public DeviceAcquisitionRoute Resolve(Device device) =>
            Path == AcquisitionPathKind.Standby ? DeviceAcquisitionRules.StandbyOf(device) : DeviceAcquisitionRules.PrimaryOf(device);
        public Device Apply(Device device) => DeviceAcquisitionRules.Apply(device, Resolve(device));
        public bool HasStandby(Device device) => DeviceAcquisitionRules.HasStandby(device);
        public AcquisitionPathKind Current(int deviceId) => Path;
        public event Action<int, AcquisitionPathKind, string>? PathChanged { add { } remove { } }
    }

    private sealed class RecordingLocal : ILocalCollector
    {
        public int TextWrites;
        public IReadOnlyList<CollectorDeviceStatus> GetStatuses() => [];
        public void RequestReload() { }
        public bool IsDeviceQuiescent(int deviceId) => true;
        public CollectorDeviceDiagnostics? GetDiagnostics(int deviceId) => null;
        public CollectorReconnectResult RequestReconnect(int deviceId) => CollectorReconnectResult.ConfigurationChanged;
        public Task<CommandExecutionResult> WriteAsync(int deviceId, Tag tag, double value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandExecutionResult(false, "unused"));
        public Task<CommandExecutionResult> WriteTextIdentifiedAsync(int deviceId, Tag tag, string text, CollectorCommandContext context, CancellationToken ct = default)
        {
            TextWrites++;
            return Task.FromResult(new CommandExecutionResult(true, "ok"));
        }
    }
}
