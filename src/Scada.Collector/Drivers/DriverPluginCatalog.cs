using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;

namespace Scada.Collector.Drivers;

/// <summary>
/// 外部驱动插件目录的扫描与注册表。默认不加载任何外部程序集。
/// 配置 <see cref="ScadaSettings.DriverPluginRoot"/> 后，仍须把 DLL 的 SHA-256 写入允许列表才会加载；
/// 覆盖内置协议还须在 <see cref="ScadaSettings.DriverPluginOverrideProtocols"/> 中显式声明。
/// 单个程序集加载失败只记录原因，不影响其余插件与主程序启动。
/// </summary>
public sealed class DriverPluginCatalog : IProtocolDriverAvailability
{
    private readonly ILogger<DriverPluginCatalog> _log;
    private readonly DriverHostContext _context;
    private readonly DriverPluginPolicy _policy;
    private readonly Dictionary<ProtocolType, PluginEntry> _plugins = [];
    private readonly List<DriverPluginLoadFailure> _failures = [];
    private readonly string? _root;

    private sealed record PluginEntry(IDeviceDriverProvider Provider, string AssemblyPath);

    public DriverPluginCatalog(IOptions<ScadaSettings> settings, IDeviceCredentials credentials, ILogger<DriverPluginCatalog> log, TimeProvider? clock = null)
    {
        _log = log;
        _policy = DriverPluginPolicy.From(settings.Value, DeviceDriverFactory.BuiltInProtocols);
        _context = new DriverHostContext(credentials, new DriverLoggerAdapter(log), clock);
        var configured = settings.Value.DriverPluginRoot?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            _log.LogDebug("未配置驱动插件目录，仅使用内置驱动");
            return;
        }

        _root = Path.GetFullPath(configured);
        if (!Directory.Exists(_root))
        {
            _failures.Add(new DriverPluginLoadFailure(_root, "插件目录不存在"));
            _log.LogWarning("驱动插件目录 {Root} 不存在，仅使用内置驱动", _root);
            return;
        }

        LoadDirectory(_root);
    }

    public bool LoadingEnabled => _root is not null;

    public bool HasPlugin(ProtocolType protocol) => _plugins.ContainsKey(protocol);

    public bool IsDriverAvailable(ProtocolType protocol) =>
        DeviceDriverFactory.BuiltInProtocols.Contains(protocol) || _plugins.ContainsKey(protocol);

    public bool TryCreate(ProtocolType protocol, out IDeviceDriver? driver, out string? providerName)
    {
        driver = null;
        providerName = null;
        if (!_plugins.TryGetValue(protocol, out var entry)) return false;
        providerName = entry.Provider.Name;
        driver = entry.Provider.Create(_context);
        return true;
    }

    public DriverPluginCatalogSnapshot Snapshot(IReadOnlyCollection<(ProtocolType Protocol, string Name, string Version)> builtIns)
    {
        var drivers = new List<DriverRegistration>();
        foreach (var (protocol, name, version) in builtIns)
        {
            drivers.Add(_plugins.TryGetValue(protocol, out var plugin)
                ? new DriverRegistration(protocol, plugin.Provider.Name, plugin.Provider.Version, true, plugin.AssemblyPath, true)
                : new DriverRegistration(protocol, name, version, false, null, false));
        }
        // 只由插件提供的协议（内置不实现，例如 IEC 61850）单独列出。
        foreach (var (protocol, entry) in _plugins.Where(x => builtIns.All(b => b.Protocol != x.Key)))
            drivers.Add(new DriverRegistration(protocol, entry.Provider.Name, entry.Provider.Version, true, entry.AssemblyPath, false));
        return new DriverPluginCatalogSnapshot(ScadaTime.NowMs(), _root, LoadingEnabled,
            drivers.OrderBy(x => x.Protocol).ToList(), _failures.ToList());
    }

    private void LoadDirectory(string root)
    {
        var context = new PluginLoadContext(root);
        foreach (var file in Directory.EnumerateFiles(root, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (IsHostAssembly(file)) continue;
            var sha256 = DriverPluginPolicy.FileSha256(file);
            if (!_policy.AllowsFile(sha256, out var reason))
            {
                _failures.Add(new DriverPluginLoadFailure(file, reason!));
                _log.LogWarning("拒绝加载驱动插件 {File}：{Reason}", file, reason);
                continue;
            }

            try { LoadAssembly(context, file); }
            catch (Exception ex)
            {
                var loadReason = ex is ReflectionTypeLoadException types
                    ? string.Join("; ", types.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message).Distinct())
                    : ex.Message;
                _failures.Add(new DriverPluginLoadFailure(file, loadReason));
                _log.LogError(ex, "加载驱动插件 {File} 失败：{Reason}", file, loadReason);
            }
        }
    }

    // 插件目录里若混入了主程序自己的程序集，加载它会创建第二份宿主类型，必须跳过。
    private static bool IsHostAssembly(string path) =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, Path.GetFileName(path)));

    private void LoadAssembly(AssemblyLoadContext context, string path)
    {
        var assembly = context.LoadFromAssemblyPath(path);
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || !typeof(IDeviceDriverProvider).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                _failures.Add(new DriverPluginLoadFailure(path, $"{type.FullName} 缺少公开无参构造器"));
                _log.LogWarning("驱动插件类型 {Type} 缺少公开无参构造器，已跳过", type.FullName);
                continue;
            }

            var provider = (IDeviceDriverProvider)Activator.CreateInstance(type)!;
            if (!_policy.AllowsProtocol(provider.Protocol, out var protocolReason))
            {
                _failures.Add(new DriverPluginLoadFailure(path, protocolReason!));
                _log.LogWarning("拒绝注册驱动插件 {Name}（{File}）：{Reason}", provider.Name, path, protocolReason);
                continue;
            }
            if (_plugins.TryGetValue(provider.Protocol, out var existing))
            {
                var reason = $"协议 {provider.Protocol} 已由 {existing.Provider.Name}（{existing.AssemblyPath}）提供，忽略 {provider.Name}（{path}）";
                _failures.Add(new DriverPluginLoadFailure(path, reason));
                _log.LogWarning("驱动插件协议冲突：{Reason}", reason);
                continue;
            }

            _plugins[provider.Protocol] = new PluginEntry(provider, path);
            _log.LogInformation("已加载驱动插件 {Name} {Version}（协议 {Protocol}，程序集 {File}）", provider.Name, provider.Version, provider.Protocol, path);
        }
    }
}
