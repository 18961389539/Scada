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
