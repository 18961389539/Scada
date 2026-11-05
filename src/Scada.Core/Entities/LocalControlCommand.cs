namespace Scada.Core.Entities;

/// <summary>
/// 本机设备的控制命令。与网关命令共用“先落库再下发”：相同编号只执行一次，结果未知时不再写。
/// </summary>
public sealed class LocalControlCommand
{
    public Guid Id { get; set; }
    public int DeviceId { get; set; }
    public int TagId { get; set; }
    public double Value { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string State { get; set; } = "Pending";
    public int Attempt { get; set; }
    public string? ResultJson { get; set; }
    public long CreatedTs { get; set; }
    public long? CompletedTs { get; set; }
}
