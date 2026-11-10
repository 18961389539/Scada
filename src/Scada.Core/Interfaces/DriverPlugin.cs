using Scada.Core.Entities;
using Scada.Core.Enums;

namespace Scada.Core.Interfaces;

/// <summary>
/// 驱动插件的宿主上下文。插件只依赖 Scada.Core，日志与协议运行时按需用 <see cref="GetService{T}"/> 解析，
/// 因此契约本身不引入任何第三方包。
/// </summary>
public sealed class DriverHostContext(IServiceProvider services, IDeviceCredentials credentials)
{
    public IServiceProvider Services { get; } = services;
    public IDeviceCredentials Credentials { get; } = credentials;

    public T? GetService<T>() where T : class => Services.GetService(typeof(T)) as T;
}

/// <summary>
/// 外部驱动插件的入口。实现类必须有公开无参构造器，由宿主在启动时从 <c>Scada:DriverPluginRoot</c>  discovery。
///
/// 插件按协议槽位提供驱动：声明的 <see cref="Protocol"/> 覆盖同名内置驱动，未声明的协议不受影响。
/// 这样接入方可以用商业许可库替换 IEC 61850 等内置缺失的实现，而不必重编译主程序。
/// 新增协议枚举值不属于插件能力范围——那需要同时改动前端协议表单与校验链。
/// </summary>
public interface IDeviceDriverProvider
{
    ProtocolType Protocol { get; }

    /// <summary>诊断页展示用的驱动名，例如 "Contoso IEC 61850 MMS"。</summary>
    string Name { get; }

    string Version { get; }

    /// <summary>每次为设备创建驱动时调用；实现必须返回新实例，不得缓存复用。</summary>
    IDeviceDriver Create(DriverHostContext context);
}