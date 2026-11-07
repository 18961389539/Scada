using System.Collections.Concurrent;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Simulation;

namespace Scada.Collector.Simulation;

public sealed record SimulationItem(int TagId, string Name, TagDataType DataType, double Value, bool Forced, TagQuality Quality, string? Error);
public sealed record SimulationSnapshot(int DeviceId, bool Enabled, SimulationFaultKind Fault, long FaultUntilTs, IReadOnlyList<SimulationItem> Items);

/// <summary>
/// 设备级仿真状态：波形生成、IO 强制、故障注入。仿真值仍走正常采集/报警/历史链路，
/// 因此离线调试看到的是与真实设备一致的实时值、质量和事件。
/// </summary>
public sealed class SimulationRuntime
{
    private sealed class DeviceState
    {
        public readonly long StartedTs = ScadaTime.NowMs();
        public readonly object Gate = new();
        public readonly Dictionary<int, double> Forced = new();
        public readonly Dictionary<int, double> Last = new();
        public SimulationFaultKind Fault = SimulationFaultKind.None;
        public long FaultUntilTs;
        public Random? Random;
    }

    private readonly ConcurrentDictionary<int, DeviceState> _devices = new();

    private DeviceState State(int deviceId) => _devices.GetOrAdd(deviceId, _ => new DeviceState());

    public IReadOnlyList<RawTagValue> Read(Device device, SimulationDeviceOptions options, IReadOnlyList<Tag> tags)
    {
        var state = State(device.Id);
        var now = ScadaTime.NowMs();
        lock (state.Gate)
        {
            var fault = ActiveFault(state, now);
            if (fault == SimulationFaultKind.CommFailure) throw new IOException("仿真故障：通信中断");
            if (fault == SimulationFaultKind.BadQuality)
                return tags.Select(tag => new RawTagValue(tag.Id, 0, TagQuality.Bad, null, "仿真故障：坏质量")).ToList();

            var values = new List<RawTagValue>(tags.Count);
            foreach (var tag in tags)
            {
                double value;
                if (state.Forced.TryGetValue(tag.Id, out var forced)) value = forced;
                else if (fault == SimulationFaultKind.Frozen && state.Last.TryGetValue(tag.Id, out var last)) value = last;
                else value = Generate(tag, options, state, now);
                state.Last[tag.Id] = value;
                values.Add(new RawTagValue(tag.Id, value));
            }
            return values;
        }
    }

    public void Write(int deviceId, int tagId, double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("仿真写入值不是有限数值");
        var state = State(deviceId);
        lock (state.Gate)
        {
            var fault = ActiveFault(state, ScadaTime.NowMs());
            if (fault is SimulationFaultKind.CommFailure or SimulationFaultKind.BadQuality)
                throw new IOException(fault == SimulationFaultKind.CommFailure ? "仿真故障：通信中断，命令未下发" : "仿真故障：坏质量，命令未下发");
            state.Forced[tagId] = value;
            state.Last[tagId] = value;
        }
    }

    public void Force(int deviceId, int tagId, double value) => Write(deviceId, tagId, value);

    public bool Release(int deviceId, int tagId)
    {
        var state = State(deviceId);
        lock (state.Gate) return state.Forced.Remove(tagId);
    }

    public int ReleaseAll(int deviceId)
    {
        var state = State(deviceId);
        lock (state.Gate)
        {
            var count = state.Forced.Count;
            state.Forced.Clear();
            return count;
        }
    }

    public void InjectFault(int deviceId, SimulationFaultKind kind, int durationSec)
    {
        var state = State(deviceId);
        lock (state.Gate)
        {
            if (kind == SimulationFaultKind.None || durationSec <= 0)
            {
                state.Fault = SimulationFaultKind.None;
                state.FaultUntilTs = 0;
            }
            else
            {
                state.Fault = kind;
                state.FaultUntilTs = ScadaTime.NowMs() + durationSec * 1000L;
            }
        }
    }

    public void ClearFault(int deviceId) => InjectFault(deviceId, SimulationFaultKind.None, 0);

    public SimulationSnapshot Snapshot(Device device, SimulationDeviceOptions options, IReadOnlyList<Tag> tags)
    {
        var state = State(device.Id);
        var now = ScadaTime.NowMs();
        lock (state.Gate)
        {
            var fault = ActiveFault(state, now);
            var items = new List<SimulationItem>(tags.Count);
            foreach (var tag in tags)
            {
                var forced = state.Forced.TryGetValue(tag.Id, out var forcedValue);
                var value = forced ? forcedValue : fault switch
                {
                    SimulationFaultKind.BadQuality or SimulationFaultKind.CommFailure => 0,
                    SimulationFaultKind.Frozen when state.Last.TryGetValue(tag.Id, out var last) => last,
                    _ => Generate(tag, options, state, now)
                };
                var quality = fault switch
                {
                    SimulationFaultKind.CommFailure => TagQuality.CommFailure,
                    SimulationFaultKind.BadQuality => TagQuality.Bad,
                    _ => TagQuality.Good
                };
                var error = fault switch
                {
                    SimulationFaultKind.CommFailure => "仿真故障：通信中断",
                    SimulationFaultKind.BadQuality => "仿真故障：坏质量",
                    _ => null
                };
                items.Add(new SimulationItem(tag.Id, tag.Name, tag.DataType, value, forced, quality, error));
            }
            return new SimulationSnapshot(device.Id, device.SimulationEnabled, fault, state.FaultUntilTs, items);
        }
    }

    public void Remove(int deviceId) => _devices.TryRemove(deviceId, out _);

    private static SimulationFaultKind ActiveFault(DeviceState state, long now)
    {
        if (state.Fault != SimulationFaultKind.None && now >= state.FaultUntilTs)
        {
            state.Fault = SimulationFaultKind.None;
            state.FaultUntilTs = 0;
        }
        return state.Fault;
    }

    private static double Generate(Tag tag, SimulationDeviceOptions options, DeviceState state, long now)
    {
        var elapsedMs = Math.Max(0, now - state.StartedTs);
        var periodMs = Math.Max(1, options.PeriodSec) * 1000.0;
        var phase = (tag.Id % 97) / 97.0;
        var t = elapsedMs / periodMs + phase;

        if (tag.DataType == TagDataType.Bool)
        {
            var on = options.Waveform switch
            {
                SimulationWaveform.Constant => false,
                SimulationWaveform.Counter => (Math.Floor(t) % 2) == 0,
                SimulationWaveform.Random => StableRandom(options.Seed, tag.Id, (long)Math.Floor(t)) >= 0.5,
                SimulationWaveform.Square => (Math.Floor(t) % 2) == 0,
                _ => Math.Sin(t * 2 * Math.PI) >= 0
            };
            return on ? 1 : 0;
        }

        var (min, max) = SimulationOptions.Range(tag, options);
        var normalized = options.Waveform switch
        {
            SimulationWaveform.Sine => (Math.Sin(t * 2 * Math.PI) + 1) / 2,
            SimulationWaveform.Square => Math.Floor(t) % 2 == 0 ? 1 : 0,
            SimulationWaveform.Triangle => 1 - Math.Abs(2 * (t - Math.Floor(t + 0.5))),
            SimulationWaveform.Counter => ((long)(elapsedMs / periodMs) % 10) / 10.0,
            SimulationWaveform.Random => StableRandom(options.Seed, tag.Id, (long)Math.Floor(t)),
            _ => 0.5
        };
        var span = max - min;
        var value = min + normalized * span;
        if (options.NoisePercent > 0)
        {
            state.Random ??= new Random(options.Seed == 0 ? Random.Shared.Next() : options.Seed);
            value += (state.Random.NextDouble() - 0.5) * span * options.NoisePercent / 100;
        }
        return Round(tag.DataType, value, min, max);
    }

    private static double Round(TagDataType type, double value, double min, double max)
    {
        value = Math.Clamp(value, min, max);
        return type switch
        {
            TagDataType.Int16 => Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue),
            TagDataType.UInt16 => Math.Clamp(Math.Round(value), ushort.MinValue, ushort.MaxValue),
            TagDataType.Int32 => Math.Clamp(Math.Round(value), int.MinValue, int.MaxValue),
            TagDataType.UInt32 => Math.Clamp(Math.Round(value), uint.MinValue, uint.MaxValue),
            _ => value
        };
    }

    private static double StableRandom(int seed, int tagId, long bucket)
    {
        unchecked
        {
            var value = (uint)(seed * 374761393 + tagId * 668265263 + (int)bucket * 2147483647);
            value = (value ^ (value >> 13)) * 1274126177u;
            value ^= value >> 16;
            return (value & 0x7fffffff) / (double)0x7fffffff;
        }
    }
}
