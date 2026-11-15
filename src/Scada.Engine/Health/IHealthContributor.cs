using Scada.Engine.Maintenance;

namespace Scada.Engine.Health;

/// <summary>
/// 健康告警贡献者：由各个后台服务自己报告异常，健康聚合只负责收集。
///
/// 引入它是为了去掉 <c>HealthSnapshotService</c> 里的服务定位器——此前那里用
/// <c>IServiceProvider.GetService&lt;T&gt;()</c> 逐个反查 6 个具体类型，等于在组合根之外
/// 隐藏了一张依赖图，任何构造函数改动都要靠运行时才发现。改成接口集合后依赖方向正过来：
/// 服务依赖接口，聚合只依赖 <c>IEnumerable&lt;IHealthContributor&gt;</c>。
///
/// 实现约定：
/// <list type="bullet">
///   <item>必须是单例。方法会被 <c>/health</c> 与 <c>/metrics</c> 高频调用。</item>
///   <item>必须无副作用、不阻塞。只读字段并返回快照，不做 IO、不加锁等待。</item>
///   <item>无告警时返回空集合而不是 null。</item>
/// </list>
/// </summary>
public interface IHealthContributor
{
    IEnumerable<SystemWarning> HealthWarnings();
}
