namespace Scada.Core.Entities;

public class OperationLog
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public long Ts { get; set; }
    public string Action { get; set; } = string.Empty;
    public int? TagId { get; set; }
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Result { get; set; } = string.Empty;
    // 审计对象与字段差分（JSON），配置类变更填写。
    public string? TargetType { get; set; }
    public string? TargetId { get; set; }
    public string? Details { get; set; }
}
