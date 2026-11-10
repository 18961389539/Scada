using Scada.Core.Enums;

namespace Scada.Core.Interfaces;

/// <summary>
/// 插件可用的日志面。故意不暴露 Microsoft.Extensions 类型，插件只依赖 Scada.Core。
/// </summary>
public interface IDriverLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message, Exception? exception = null);
    void Error(string message, Exception? exception = null);
}

/// <summary>
/// 驱动插件的宿主上下文。只提供凭据、日志和时间，不暴露 IServiceProvider 或配置库。
/// </summary>
public sealed class DriverHostContext
{
    public DriverHostContext(IDeviceCredentials credentials, IDriverLogger logger, TimeProvider? clock = null)
    {
        Credentials = credentials;
        Logger = logger;
        Clock = clock ?? TimeProvider.System;
    }

    public IDeviceCredentials Credentials { get; }
    public IDriverLogger Logger { get; }
    public TimeProvider Clock { get; }
}

/// <summary>
/// 查询某协议当前是否有可用的采集驱动（内置或已按策略加载的插件）。
/// 用于校验层，避免进程级静态开关让测试与宿主互相污染。
/// </summary>
public interface IProtocolDriverAvailability
{
    bool IsDriverAvailable(ProtocolType protocol);
}

/// <summary>
/// 外部驱动插件的入口。实现类必须有公开无参构造器，由宿主在启动时从 <c>Scada:DriverPluginRoot</c> 加载。
///
/// 插件按协议槽位提供驱动。覆盖已有内置协议必须在 <c>Scada:DriverPluginOverrideProtocols</c> 中显式声明；
/// 未在允许列表中的程序集（按 SHA-256）默认不会被加载。
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
