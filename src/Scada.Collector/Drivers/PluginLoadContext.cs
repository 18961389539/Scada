using System.Reflection;
using System.Runtime.Loader;

namespace Scada.Collector.Drivers;

/// <summary>
/// 插件程序集的加载上下文。两条统一规则保证接口类型不分裂：
/// 宿主已加载的同名程序集直接复用；宿主应用目录中存在的程序集一律回落到默认上下文。
/// 只有插件自带的私有依赖才从插件目录加载。
/// </summary>
internal sealed class PluginLoadContext(string pluginRoot) : AssemblyLoadContext(isCollectible: true)
{
    private readonly string _pluginRoot = pluginRoot;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { Length: > 0 } name) return null;

        var loaded = Default.Assemblies.FirstOrDefault(x => string.Equals(x.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (loaded is not null) return loaded;

        // 宿主随包发布的依赖（Microsoft.Extensions.* / Serilog 等）必须与宿主共享实例，
        // 否则 ILoggerFactory、IServiceProvider 这类类型在插件侧会变成另一个类型。
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, name + ".dll"))) return Default.LoadFromAssemblyName(assemblyName);

        var candidate = Path.Combine(_pluginRoot, name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }
}