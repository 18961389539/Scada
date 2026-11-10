using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Protocols;

namespace Scada.Collector.Drivers;

/// <summary>
/// 外部驱动插件目录的扫描与注册表。目录默认为空（不加载任何外部代码）；
/// 配置 <see cref="ScadaSettings.DriverPluginRoot"/> 后，启动时加载其中的程序集。
/// 单个程序集加载失败只记录原因，不影响其余插件与主程序启动。
/// </summary>
public sealed class DriverPluginCatalog
{
    private readonly ILogger<DriverPluginCatalog> _log;
    private readonly DriverHostContext _context;
    private readonly Dictionary<ProtocolType, PluginEntry> _plugins = [];
    private readonly List<DriverPluginLoadFailure> _failures = [];
    private readonly string? _root;

    private sealed record PluginEntry(IDeviceDriverProvider Provider, string AssemblyPath);

    public DriverPluginCatalog(IOptions<ScadaSettings> settings, IServiceProvider services, IDeviceCredentials credentials, ILogger<DriverPluginCatalog> log)
    {
        _log = log;
        _context = new DriverHostContext(services, credentials);
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
        // 只有真的加载到 IEC 61850 provider 才放开设备启用，否则采集器会陷入连接必失败的退避重连。
        Iec61850Spec.SetClientAvailable(_plugins.ContainsKey(ProtocolType.Iec61850));
    }

    public bool LoadingEnabled => _root is not null;

    public bool HasPlugin(ProtocolType protocol) => _plugins.ContainsKey(protocol);

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
            try { LoadAssembly(context, file); }
            catch (Exception ex)
            {
                var reason = ex is ReflectionTypeLoadException types
                    ? string.Join("; ", types.LoaderExceptions.Where(x => x is not null).Select(x => x!.Message).Distinct())
                    : ex.Message;
                _failures.Add(new DriverPluginLoadFailure(file, reason));
                _log.LogError(ex, "加载驱动插件 {File} 失败：{Reason}", file, reason);
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