using System.Text.Json;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Protocols;

namespace Scada.Core.Simulation;

public enum SimulationWaveform { Sine, Square, Triangle, Counter, Random, Constant }
public enum SimulationFaultKind { None, CommFailure, BadQuality, Frozen }

/// <summary>虚拟设备/离线调试的波形参数；测点自身的量程优先于这里的范围。</summary>
public sealed class SimulationDeviceOptions
{
    public SimulationWaveform Waveform { get; set; } = SimulationWaveform.Sine;
    public int PeriodSec { get; set; } = 60;
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>叠加在波形上的噪声百分比（0～50）。</summary>
    public double NoisePercent { get; set; }
    public int Seed { get; set; }
}

public static class SimulationOptions
{
    public static SimulationDeviceOptions Parse(Device device)
    {
        if (string.IsNullOrWhiteSpace(device.SimulationOptions)) return new SimulationDeviceOptions();
        try { return JsonSerializer.Deserialize<SimulationDeviceOptions>(device.SimulationOptions, ProtocolCatalog.OptionsJson) ?? new SimulationDeviceOptions(); }
        catch (JsonException ex) { throw new FormatException($"仿真参数 JSON 无效：{ex.Message}", ex); }
    }

    public static string? Validate(Device device)
    {
        var json = device.SimulationOptions ?? "";
        if (json.Length > 16000) return "仿真参数过长（最多 16000 字符）";
        if (device.SimulationEnabled && device.GatewayId is not null) return "仿真设备只能由中心本机采集，不能分配给网关";
        if (string.IsNullOrWhiteSpace(json)) return null;
        SimulationDeviceOptions options;
        try { options = Parse(device); }
        catch (FormatException ex) { return ex.Message; }
        if (options.PeriodSec is < 1 or > 86400) return "仿真周期须为 1～86400 秒";
        if (!double.IsFinite(options.NoisePercent) || options.NoisePercent is < 0 or > 50) return "仿真噪声须为 0～50%";
        if ((options.Min is null) != (options.Max is null)) return "仿真范围必须同时填写最小值和最大值";
        if (options.Min is double min && options.Max is double max)
        {
            if (!double.IsFinite(min) || !double.IsFinite(max)) return "仿真范围必须是有限值";
            if (min >= max) return "仿真最大值须大于最小值";
        }
        return null;
    }

    public static (double Min, double Max) Range(Tag tag, SimulationDeviceOptions options)
    {
        if (tag.MinValue is double tagMin && tag.MaxValue is double tagMax && double.IsFinite(tagMin) && double.IsFinite(tagMax) && tagMin < tagMax)
            return (tagMin, tagMax);
        if (options.Min is double min && options.Max is double max) return (min, max);
        return tag.DataType == TagDataType.Bool ? (0, 1) : (0, 100);
    }
}
