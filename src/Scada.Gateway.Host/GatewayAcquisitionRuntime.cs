using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Collector;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Collector.Opc;
using System.Text.Json;

namespace Scada.Gateway.Hosting;

public interface IGatewayAcquisitionRuntime
{
    GatewayConfiguration? Configuration { get; }
    IReadOnlyList<CollectorDeviceStatus> Statuses { get; }
    bool HasFaultedWorker { get; }
    void RetryFaultedWorkers();
    void Validate(GatewayConfiguration configuration);
    Task StartAsync(GatewayConfiguration configuration, CancellationToken ct);
    Task StopAsync();
    Task<JsonElement> EngineerAsync(GatewayEngineeringRequest request, CancellationToken ct) => throw new NotSupportedException();
    Task<CommandExecutionResult> WriteAsync(GatewayCommand command, CancellationToken ct) => Task.FromResult(new CommandExecutionResult(false, "运行实例不支持命令", Outcome: GatewayCommandOutcome.NotSent));
}

public sealed class GatewayAcquisitionRuntime(GatewayAgentOptions options, GatewayAgentStore store, IDataProtectionProvider protection) : IGatewayAcquisitionRuntime
{
    private IHost? _host;
    private CollectorEngine? _engine;
    private GatewayConfiguration? _configuration;
    public GatewayConfiguration? Configuration => _configuration;
    public IReadOnlyList<CollectorDeviceStatus> Statuses => _engine?.GetStatuses() ?? [];
    public bool HasFaultedWorker => _configuration?.Devices.Any(x => x.Device.Enabled && _engine?.GetDiagnostics(x.Device.Id)?.State == "Faulted") == true;
    public void RetryFaultedWorkers() => _engine?.RequestReload();
    public async Task<CommandExecutionResult> WriteAsync(GatewayCommand command, CancellationToken ct)
    {
        var configuration = _configuration; var engine = _engine;
        var tag = configuration?.Tags.SingleOrDefault(x => x.Id == command.TagId && x.DeviceId == command.DeviceId);
        if (engine is null || configuration is null || configuration.Revision != command.ConfigurationRevision || tag?.Writable != true
            || !configuration.Devices.Any(x => x.Device.Id == command.DeviceId && x.Device.Enabled)
            || !CommandValuePolicy.TryGetRawValue(tag, command.Value, out _, out _))
            return new(false, "命令与当前可写配置不一致，未下发", Outcome: GatewayCommandOutcome.NotSent);
        return await engine.WriteAsync(command.DeviceId, tag, command.Value, ct);
    }

    public void Validate(GatewayConfiguration configuration)
    {
        if (!GatewayProtocol.IsRevision(configuration.Revision) || configuration.Devices is null || configuration.Tags is null || configuration.Devices.Count > 5000
            || configuration.Devices.Any(x => x is null || x.Device is null || x.Device.Id < 1 || x.Device.GatewayId != options.GatewayId || x.OpcPassword?.Length > 4096)
            || configuration.Devices.Select(x => x.Device.Id).Distinct().Count() != configuration.Devices.Count
            || configuration.Tags.Any(x => x is null || x.Id < 1) || configuration.Tags.Select(x => x.Id).Distinct().Count() != configuration.Tags.Count)
            throw new InvalidDataException("中心配置的标识、设备或测点无效");
        var devices = configuration.Devices.ToDictionary(x => x.Device.Id, x => x.Device);
        foreach (var device in devices.Values) if (ConfigurationValidator.Device(device) is not null) throw new InvalidDataException("中心设备连接配置无效");
        foreach (var tag in configuration.Tags)
            if (!devices.TryGetValue(tag.DeviceId, out var device) || ConfigurationValidator.Tag(tag, device) is not null) throw new InvalidDataException("中心测点地址或设备归属无效");
        if (configuration.Tags.GroupBy(x => x.DeviceId).Any(x => x.Count() > GatewayProtocol.MaxValuesPerBatch)) throw new InvalidDataException("单设备测点数超过协议批次限制");
    }

    public async Task StartAsync(GatewayConfiguration configuration, CancellationToken ct)
    {
        if (_host is not null) throw new InvalidOperationException("旧采集实例尚未停止");
        Validate(configuration);
        var credentials = new AgentCredentials(protection.CreateProtector("Scada.Gateway.DevicePassword.v1"));
        var devices = configuration.Devices.Select(item =>
        {
            var device = item.Device.Clone(); device.GatewayId = null;
            device.OpcPasswordProtected = item.OpcPassword is null ? null : credentials.Protect(item.OpcPassword); return device;
        }).ToArray();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = options.DataRoot });
        // Driver exception details remain local diagnostics; the parent emits redacted state only.
        builder.Logging.ClearProviders(); builder.Services.AddSingleton<ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        builder.Services.AddSingleton<IHostLifetime, SilentLifetime>();
        builder.Services.Configure<HostOptions>(x => x.ShutdownTimeout = TimeSpan.FromSeconds(5));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Scada:ConfigDbPath"] = Path.Combine(options.DataRoot, "collector-config"), ["Scada:OpcPkiRoot"] = Path.Combine(options.DataRoot, "pki")
        });
        builder.Services.Configure<ScadaSettings>(x => x.CommandTimeoutMs = options.CommandTimeoutMs);
        builder.Services.AddSingleton<IConfigSnapshot>(new Snapshot(devices, configuration.Tags.Select(x => x.Clone()).ToArray()));
        builder.Services.AddSingleton<IDeviceCredentials>(credentials);
        builder.Services.AddSingleton<IRealtimeCache, SampleCache>(); builder.Services.AddSingleton<IRealtimeBus, NullBus>();
        builder.Services.AddSingleton<IAlarmSink, JournalOnlyAlarms>();
        builder.Services.AddSingleton<ICollectorJournal>(new GatewayCollectorJournal(store, configuration.Revision));
        builder.Services.AddScadaCollector(builder.Configuration);
        builder.Services.AddSingleton<OpcCertificateService>(); builder.Services.AddSingleton<OpcBrowseRegistry>(); builder.Services.AddHostedService<OpcBrowseRegistryCleanup>();
        var host = builder.Build(); _host = host; _engine = host.Services.GetRequiredService<CollectorEngine>(); _configuration = configuration;
        try { await host.StartAsync(ct); }
        catch { await StopAsync(); throw; }
    }

    // Never release the installation lease or start a replacement while old I/O is still alive.
    public async Task StopAsync()
    {
        var host = _host; var engine = _engine; var configuration = _configuration;
        if (host is null) return;
        try { await host.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        if (engine is not null && configuration is not null)
            while (configuration.Devices.Any(x => !engine.IsDeviceQuiescent(x.Device.Id))) await Task.Delay(100);
        if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync(); else host.Dispose();
        _engine = null; _host = null; _configuration = null;
    }

    public async Task<JsonElement> EngineerAsync(GatewayEngineeringRequest request, CancellationToken ct)
    {
        var host = _host; var configuration = _configuration;
        if (host is null || configuration?.Revision != request.ConfigurationRevision || !host.Services.GetRequiredService<IConfigSnapshot>().TryGetDevice(request.DeviceId, out var device))
            throw new InvalidOperationException("工程操作配置或设备归属已变化");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(device.TimeoutMs); ct = deadline.Token;
        object result;
        switch (request.Operation)
        {
            case GatewayEngineeringOperation.Probe:
                result = await DeviceProbe.ReadAsync(device, request.Payload.Deserialize<DeviceProbeRequest>(GatewayProtocol.Json), host.Services.GetRequiredService<IDeviceDriverFactory>(), host.Services.GetRequiredService<IReadPlanBuilder>(), ct); break;
            case GatewayEngineeringOperation.Diagnostics:
            case GatewayEngineeringOperation.DiagnosticsExport:
                var snapshot = (_engine!.GetDiagnostics(device.Id) ?? throw new KeyNotFoundException("设备诊断不存在")) with { GatewayId = options.GatewayId, ConfigurationRevision = configuration.Revision };
                result = request.Operation == GatewayEngineeringOperation.DiagnosticsExport ? snapshot
                    : CollectorDiagnosticPaging.Page(snapshot, request.Payload.Deserialize<CollectorDiagnosticQuery>(GatewayProtocol.Json) ?? new()); break;
            case GatewayEngineeringOperation.Reconnect:
                if (!_engine!.RequestReconnect(device.Id)) throw new InvalidOperationException("设备停用或没有测点，无可重连的采集任务");
                result = new { message = "已请求在当前采集及命令结束后重连" }; break;
            default:
                if (device.Protocol != Scada.Core.Enums.ProtocolType.OpcUa) throw new ArgumentException("此设备不是 OPC UA 协议");
                var opc = host.Services.GetRequiredService<OpcUaRuntime>(); var catalog = host.Services.GetRequiredService<OpcBrowseRegistry>(); var certs = host.Services.GetRequiredService<OpcCertificateService>();
                switch (request.Operation)
                {
                    case GatewayEngineeringOperation.OpcBrowse:
                        result = await catalog.BrowseAsync(request.UserId, device, configuration.Revision, request.Payload.Deserialize<OpcBrowseRequest>(GatewayProtocol.Json) ?? throw new ArgumentException("浏览参数无效"), ct); break;
                    case GatewayEngineeringOperation.OpcClose:
                        await catalog.CloseAsync(request.UserId, device.Id, request.Payload.GetProperty("cursor").GetString() ?? "", ct); result = new { closed = true }; break;
                    case GatewayEngineeringOperation.OpcMetadata:
                        var nodes = request.Payload.Deserialize<string[]>(GatewayProtocol.Json);
                        if (nodes is null || nodes.Length is < 1 or > 500 || nodes.Any(x => !OpcNodeAddress.IsValid(x))) throw new ArgumentException("每次须读取 1～500 个有效变量的元数据");
                        await using (var session = new OpcCatalogSession(opc, await opc.ConnectAsync(device, ct))) result = await session.MetadataAsync(nodes, ct);
                        break;
                    case GatewayEngineeringOperation.OpcCertificates: result = await certs.ListAsync(ct); break;
                    case GatewayEngineeringOperation.OpcPublicCertificate: result = await certs.PublicCertificateAsync(request.Payload.GetProperty("sha256").GetString() ?? "", ct); break;
                    case GatewayEngineeringOperation.OpcTrust:
                    case GatewayEngineeringOperation.OpcUntrust:
                        try { await certs.SetTrustAsync(request.Payload.GetProperty("sha256").GetString() ?? "", request.Operation == GatewayEngineeringOperation.OpcTrust, ct); }
                        finally { await catalog.InvalidateAsync(); _engine!.RequestReload(); }
                        result = new { changed = true }; break;
                    default: throw new ArgumentException("不支持的工程操作");
                }
                break;
        }
        return JsonSerializer.SerializeToElement(result, GatewayProtocol.Json);
    }

    private sealed class AgentCredentials(IDataProtector protector) : IDeviceCredentials
    { public string Protect(string password) => protector.Protect(password); public string Unprotect(string password) => protector.Unprotect(password); }
    private sealed class SilentLifetime : IHostLifetime
    { public Task WaitForStartAsync(CancellationToken ct) => Task.CompletedTask; public Task StopAsync(CancellationToken ct) => Task.CompletedTask; }
    private sealed class JournalOnlyAlarms : IAlarmSink
    {
        private int _next;
        public Task<int> RaiseDeviceOfflineAsync(Device device, string message, long ts, CancellationToken ct = default) => Task.FromResult(Interlocked.Increment(ref _next));
        public Task RecoverDeviceOfflineAsync(int id, long ts, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class NullBus : IRealtimeBus
    { public int Backlog => 0; public void Publish(TagValue value) { } public ChannelReader<TagValue> Subscribe() => Channel.CreateUnbounded<TagValue>().Reader; }
    private sealed class SampleCache : IRealtimeCache
    {
        private readonly ConcurrentDictionary<int, TagValue> _values = new();
        public void Set(TagValue value) => _values[value.TagId] = value;
        public TagValue? Get(int id) => _values.GetValueOrDefault(id);
        public IReadOnlyList<TagValue> GetMany(IEnumerable<int> ids) => ids.Select(Get).Where(x => x is not null).Cast<TagValue>().ToList();
        public IReadOnlyDictionary<int, TagValue> Snapshot() => new Dictionary<int, TagValue>(_values);
    }
    private sealed class Snapshot(IReadOnlyList<Device> devices, IReadOnlyList<Tag> tags) : IConfigSnapshot
    {
        private readonly Dictionary<int, Device> _devices = devices.ToDictionary(x => x.Id); private readonly Dictionary<int, Tag> _tags = tags.ToDictionary(x => x.Id);
        public IReadOnlyList<Device> Devices => devices; public IReadOnlyList<Tag> Tags => tags; public IReadOnlyList<AlarmRule> AlarmRules => [];
        public int Version => 1; public long LoadedAt { get; } = ScadaTime.NowMs(); public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
        public bool TryGetTag(int id, out Tag tag) => _tags.TryGetValue(id, out tag!); public bool TryGetDevice(int id, out Device device) => _devices.TryGetValue(id, out device!);
    }
}
