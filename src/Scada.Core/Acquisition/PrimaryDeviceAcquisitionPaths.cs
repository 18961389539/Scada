using Scada.Core.Entities;

namespace Scada.Core.Acquisition;

// 未接入路径服务时的默认实现：永远走主路径，便于采集测试在不注册服务时保持原行为。
public sealed class PrimaryDeviceAcquisitionPaths : IDeviceAcquisitionPaths
{
    public static readonly PrimaryDeviceAcquisitionPaths Instance = new();

    public DeviceAcquisitionRoute Resolve(Device device) => DeviceAcquisitionRules.PrimaryOf(device);
    public Device Apply(Device device) => device;
    public bool HasStandby(Device device) => false;
    public AcquisitionPathKind Current(int deviceId) => AcquisitionPathKind.Primary;
    public event Action<int, AcquisitionPathKind, string>? PathChanged
    {
        add { }
        remove { }
    }
}
