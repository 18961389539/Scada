namespace Scada.Core.Security;

/// <summary>
/// 标记"字段值不能进审计日志"的实体属性。审计差分按特性收集，
/// 新增密钥类字段时只要加上这个特性就默认脱敏；忘了加的由
/// <c>AuditSensitivityTests</c> 一类命名单扫描拦下（fail-closed）。
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true)]
public sealed class SensitiveAttribute : Attribute
{
}
