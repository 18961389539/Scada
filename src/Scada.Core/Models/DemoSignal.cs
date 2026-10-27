using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Enums;

namespace Scada.Core.Models;

public sealed record DemoSignal(
    TagArea Area, int Address, double Value, double Amplitude = 0,
    double PeriodSeconds = 60, double Phase = 0, double IncrementPerHour = 0,
    int ToggleSeconds = 0, double Noise = 0)
{
    public double ValueAt(double elapsedSeconds)
    {
        if (ToggleSeconds > 0)
            return ((long)Math.Floor(elapsedSeconds / ToggleSeconds) & 1) == 0 ? Value : 1 - Value;
        var value = Value + Amplitude * Math.Sin(elapsedSeconds * 2 * Math.PI / PeriodSeconds + Phase)
            + Math.Floor(IncrementPerHour * elapsedSeconds / 3600);
        // 确定性噪声：同一时刻的历史采样与实时读数保持一致，避免曲线上出现不可复现的抖动。
        return Noise > 0 ? value + Noise * (PseudoNoise(elapsedSeconds) * 2 - 1) : value;
    }

    private static double PseudoNoise(double elapsedSeconds)
    {
        var x = Math.Sin(elapsedSeconds * 12.9898) * 43758.5453;
        return x - Math.Floor(x);
    }
}

public static class DemoSignals
{
    public static IReadOnlyList<DemoSignal> All { get; } = Load<DemoSignal>("signals");
    public static IReadOnlyList<DemoInspection> Inspections { get; } = Load<DemoInspection>("traceability");

    private static IReadOnlyList<T> Load<T>(string name)
    {
        using var stream = typeof(DemoSignals).Assembly.GetManifestResourceStream($"Scada.Core.Demo.{name}.json")
            ?? throw new InvalidOperationException("Bundled demo signal data is missing.");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter<TagArea>());
        return Array.AsReadOnly(JsonSerializer.Deserialize<T[]>(stream, options)
            ?? throw new InvalidOperationException("Bundled demo signal data is empty."));
    }
}

public sealed record DemoInspection(string Sn, Dictionary<string, double> Values);
