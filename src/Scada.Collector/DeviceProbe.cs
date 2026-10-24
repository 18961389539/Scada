using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector;

public static class DeviceProbe
{
    public static async Task<DeviceProbeResult> ReadAsync(Device device, DeviceProbeRequest? request, IDeviceDriverFactory drivers, IReadPlanBuilder plans, CancellationToken ct)
    {
        var probe = new Tag { Id = -1, DeviceId = device.Id, Name = "连接测试", Area = request?.Area ?? (device.Protocol == ProtocolType.OpcUa ? TagArea.OpcNode : device.Protocol == ProtocolType.SiemensS7 ? TagArea.DataBlock : TagArea.HoldingRegister),
            Address = request?.Address ?? 0, DataType = request?.DataType ?? TagDataType.Float, DbNumber = request?.DbNumber ?? 1, BitOffset = request?.BitOffset ?? 0, NodeId = request?.NodeId ?? "" };
        var error = ConfigurationValidator.Tag(probe, device); if (error is not null) throw new ArgumentException(error);
        var plan = plans.Build(device, [probe]); await using var driver = drivers.Create(device);
        try
        {
            await driver.ConnectAsync(device, ct); var values = await driver.ReadAsync(device, plan, ct); var value = values.FirstOrDefault();
            return value?.Quality == TagQuality.Good ? new(true, probe.Area.ToString(), probe.Address, probe.DataType.ToString(), value.Value)
                : new(true, Quality: value?.Quality, Message: value?.Error ?? "读取没有返回可用值");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new(false, Message: ex.Message); }
    }
}
