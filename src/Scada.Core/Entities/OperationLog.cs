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
}
