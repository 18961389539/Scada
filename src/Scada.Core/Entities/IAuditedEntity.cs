namespace Scada.Core.Entities;

/// <summary>
/// 需要在审计表里留痕的配置实体。实现它的实体由 <c>AuditInterceptor</c> 在保存时
/// 自动写一条增删改记录，不必在每个写入口手写 <c>OperationLogs.Add</c>。
///
/// 只给"实体级增删改"用：像"拒绝/失败/导出"这类没有实体变更的事件仍然手写，
/// 因为拦截器看不到它们。
/// </summary>
public interface IAuditedEntity
{
    /// <summary>审计行的 TargetType，沿用既有英文约定（如 "Device"）。</summary>
    string AuditTargetType { get; }

    /// <summary>动作文案里的中文类别（如 "设备"），拼成「创建设备：名称」。</summary>
    string AuditLabel { get; }

    /// <summary>动作文案里的对象名。</summary>
    string AuditName { get; }
}
