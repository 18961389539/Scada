namespace Scada.Core.Entities;

// Retained independently of device deletion until active alarms have been closed with a reason.
public class GatewayDeviceHealth
{
    public int DeviceId { get; set; }
    public int GatewayId { get; set; }
    public string ExpectedRevision { get; set; } = "";
    public long MonitoringSinceTs { get; set; }
    public Guid? SessionId { get; set; }
    public string? ObservedRevision { get; set; }
    public long ObservedTs { get; set; }
    public long ReceivedTs { get; set; }
    public long LastPollTs { get; set; }
    public bool Online { get; set; }
    public string? Error { get; set; }
    public long? OfflineSinceTs { get; set; }
    public GatewayDeviceHealth Clone() => (GatewayDeviceHealth)MemberwiseClone();
}
