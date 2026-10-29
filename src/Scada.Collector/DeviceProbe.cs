using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;

namespace Scada.Collector;

public static class DeviceProbe
{
    public static async Task<DeviceProbeResult> ReadAsync(Device device, DeviceProbeRequest? request, IDeviceDriverFactory drivers, IReadPlanBuilder plans, CancellationToken ct, ICollectorControl? collector = null)
    {
        var probe = new Tag { Id = -1, DeviceId = device.Id, Name = "连接测试", Area = request?.Area ?? (Scada.Core.Protocols.ProtocolCatalog.IsTextAddress(device.Protocol) ? TagArea.Address : device.Protocol == ProtocolType.OpcUa ? TagArea.OpcNode : device.Protocol == ProtocolType.SiemensS7 ? TagArea.DataBlock : TagArea.HoldingRegister),
            Address = request?.Address ?? 0, DataType = request?.DataType ?? TagDataType.Float, DbNumber = request?.DbNumber ?? 1, BitOffset = request?.BitOffset ?? 0, NodeId = request?.NodeId ?? "" };
        var error = ConfigurationValidator.Tag(probe, device); if (error is not null) throw new ArgumentException(error);

        // 设备正在采集时复用已打开的连接（同一串口不能被两个实例同时打开）；
        // null 表示未采集，回退到独立连接，保留“设备未启用也能测试”的能力。
        if (collector is not null)
        {
            CollectorProbeResult? reused = null;
            try { reused = await collector.ProbeAsync(device.Id, probe, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { reused = null; }
            if (reused is not null) return FromReused(probe, reused);
        }

        var plan = plans.Build(device, [probe]); await using var driver = drivers.Create(device);
        try
        {
            await driver.ConnectAsync(device, ct); var values = await driver.ReadAsync(device, plan, ct); var value = values.FirstOrDefault();
            return value?.Quality == TagQuality.Good ? new(true, probe.Area.ToString(), probe.Address, probe.DataType.ToString(), value.Value)
                : new(true, Quality: value?.Quality, Message: value?.Error ?? "读取没有返回可用值");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var online = collector?.GetStatuses().Any(x => x.DeviceId == device.Id && x.Online) == true;
            return new(false, Message: online
                ? $"设备正在采集，串口被采集连接占用；测试读取会复用采集连接，请稍后重试。原始错误：{ex.Message}"
                : ex.Message);
        }
    }

    private static DeviceProbeResult FromReused(Tag probe, CollectorProbeResult reused)
    {
        var area = probe.Area.ToString();
        if (reused.Error is not null) return new(false, area, probe.Address, probe.DataType.ToString(), Message: reused.Error);
        var value = reused.Value;
        if (value is null) return new(false, area, probe.Address, probe.DataType.ToString(), Message: "读取没有返回可用值");
        return value.Quality == TagQuality.Good
            ? new(true, area, probe.Address, probe.DataType.ToString(), value.Value)
            : new(true, area, probe.Address, probe.DataType.ToString(), Quality: value.Quality, Message: value.Error ?? "读取没有返回可用值");
    }
}