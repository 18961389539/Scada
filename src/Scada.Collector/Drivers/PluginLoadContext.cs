using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Scada.Collector.Drivers;

/// <summary>
/// 插件程序集的加载上下文。两条统一规则保证接口类型不分裂：
/// 宿主已加载的同名程序集直接复用；宿主应用目录中存在的程序集一律回落到默认上下文。
/// 只有插件自带的私有依赖才从插件目录加载——托管与非托管依赖都必须通过同一份 SHA-256 允许列表，
/// 否则替换依赖 DLL 就能绕过对插件主文件的校验。
/// </summary>
internal sealed class PluginLoadContext(string pluginRoot, DriverPluginPolicy policy, ILogger log) : AssemblyLoadContext(isCollectible: true)
{
    private readonly string _pluginRoot = pluginRoot;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { Length: > 0 } name) return null;

        var loaded = Default.Assemblies.FirstOrDefault(x => string.Equals(x.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null) return loaded;

        // 宿主随包发布的依赖必须与宿主共享实例，否则 Scada.Core 接口类型在插件侧会变成另一个类型。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll"))) return Default.LoadFromAssemblyName(assemblyName);

        var candidate = Path.Combine(_pluginRoot, name + ".dll");
        if (!File.Exists(candidate)) return null;
        return IsAllowed(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var candidate = Path.Combine(_pluginRoot, unmanagedDllName);
        if (!File.Exists(candidate) && !unmanagedDllName.Contains('.'))
            candidate = Path.Combine(_pluginRoot, unmanagedDllName + ".dll");
        if (!File.Exists(candidate)) return IntPtr.Zero;
        return IsAllowed(candidate) ? LoadUnmanagedDllFromPath(candidate) : IntPtr.Zero;
    }

    private bool IsAllowed(string path)
    {
        var sha256 = DriverPluginPolicy.FileSha256(path);
        if (policy.AllowsFile(sha256, out var reason)) return true;
        log.LogWarning("拒绝加载插件依赖 {File}：{Reason}", Path.GetFileName(path), reason);
        return false;
    }
}
