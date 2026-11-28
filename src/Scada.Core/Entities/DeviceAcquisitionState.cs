using Scada.Core.Acquisition;

namespace Scada.Core.Entities;

// 设备当前活动采集路径（运行库）。配置仍只描述主备，不把 Channel 改成冗余语义。
public class DeviceAcquisitionState
{
    public int DeviceId { get; set; }
    public AcquisitionPathKind ActivePath { get; set; }
    public long ChangedTs { get; set; }
    public string? ChangedBy { get; set; }
    public string? Reason { get; set; }
    public long? ActiveUnhealthySince { get; set; }
    public long? PrimaryHealthySince { get; set; }
    /// <summary>人工切到备路径后锁定，自动回切只在解锁或人工切回主机后恢复。</summary>
    public bool ManualLock { get; set; }
}
