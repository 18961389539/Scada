using Scada.Core.Enums;
using Scada.Core.Options;

namespace Scada.Engine.Alarms;

// Runtime flood decision for one raised alarm. Suppress is true when the alarm should be
// auto-suppressed by the active flood policy; the caller still decides by level.
public sealed record AlarmFloodDecision(
    int Count,
    int Threshold,
    int ExitThreshold,
    bool InFlood,
    bool Entered,
    bool Exited,
    bool Suppress);

public sealed record AlarmFloodAreaStatus(
    int? AreaId,
    int Count,
    int Threshold,
    int ExitThreshold,
    bool InFlood,
    long WindowStartTs,
    long WindowEndTs,
    long? FloodStartTs,
    int Peak,
    int? FirstOutEventId,
    int SuppressedCount);

// 运行期洪水治理：按区域统计 10 分钟报警率，超过起始阈值进入洪水、低于结束阈值退出（滞回）。
// 洪水期间低等级报警自动抑制，结束后释放；每个洪水片段标记最早一条为首出。状态只在内存，
// 重启后从新的报警重新积累，避免把历史洪水状态错误地延续到当前班次。
public sealed class AlarmFloodGuard
{
    public const long WindowMs = AlarmAnalytics.Bucket10;

    private sealed class AreaState
    {
        public Queue<long> Raises { get; } = new();
        public bool InFlood;
        public long? FloodStartTs;
        public int Peak;
        public int? FirstOutEventId;
        public HashSet<int> Suppressed { get; } = new();
    }

    private readonly ScadaSettings _settings;
    private readonly Dictionary<int, AreaState> _areas = new();
    private readonly object _gate = new();

    public AlarmFloodGuard(ScadaSettings settings)
    {
        _settings = settings;
    }

    public int StartThreshold => Math.Max(_settings.AlarmFloodThreshold, 1);
    public int ExitThreshold => Math.Max(1, Math.Min(StartThreshold, StartThreshold / 2));
    public bool AutoSuppress => _settings.AlarmFloodAutoSuppress;
    public bool FirstOutEnabled => _settings.AlarmFloodFirstOut;
    public AlarmLevel SuppressMaxLevel => _settings.AlarmFloodSuppressMaxLevel;

    public AlarmFloodDecision OnRaise(int? areaId, long now)
    {
        lock (_gate)
        {
            var state = StateOf(areaId);
            Prune(state, now);
            state.Raises.Enqueue(now);
            var count = state.Raises.Count;
            var entered = false;
            var exited = false;

            if (!state.InFlood && count > StartThreshold)
            {
                state.InFlood = true;
                state.FloodStartTs = now;
                state.Peak = count;
                state.FirstOutEventId = null;
                entered = true;
            }
            else if (state.InFlood)
            {
                if (count >= ExitThreshold) state.Peak = Math.Max(state.Peak, count);
                else
                {
                    state.InFlood = false;
                    state.FloodStartTs = null;
                    state.FirstOutEventId = null;
                    exited = true;
                }
            }

            return new AlarmFloodDecision(count, StartThreshold, ExitThreshold, state.InFlood, entered, exited, state.InFlood && AutoSuppress);
        }
    }

    // 定时推进：没有新报警时也要让洪水在计数降到结束阈值以下后退出；返回刚退出的区域。
    public IReadOnlyList<int?> Tick(long now)
    {
        lock (_gate)
        {
            var exited = new List<int?>();
            foreach (var (key, state) in _areas)
            {
                Prune(state, now);
                if (!state.InFlood || state.Raises.Count >= ExitThreshold) continue;
                state.InFlood = false;
                state.FloodStartTs = null;
                state.FirstOutEventId = null;
                exited.Add(key == 0 ? null : key);
            }
            return exited;
        }
    }

    public IReadOnlyList<AlarmFloodAreaStatus> Snapshot(long now)
    {
        lock (_gate)
        {
            var list = new List<AlarmFloodAreaStatus>();
            foreach (var (key, state) in _areas)
            {
                Prune(state, now);
                if (!state.InFlood && state.FirstOutEventId is null && state.Suppressed.Count == 0 && state.Raises.Count == 0) continue;
                list.Add(new AlarmFloodAreaStatus(
                    key == 0 ? null : key,
                    state.Raises.Count,
                    StartThreshold,
                    ExitThreshold,
                    state.InFlood,
                    now - WindowMs,
                    now,
                    state.FloodStartTs,
                    state.Peak,
                    state.FirstOutEventId,
                    state.Suppressed.Count));
            }
            return list.OrderBy(x => x.AreaId ?? 0).ToList();
        }
    }

    public bool IsInFlood(int? areaId, long now)
    {
        lock (_gate)
        {
            if (!_areas.TryGetValue(Key(areaId), out var state)) return false;
            Prune(state, now);
            return state.InFlood;
        }
    }

    public void MarkFirstOut(int? areaId, int eventId)
    {
        lock (_gate) StateOf(areaId).FirstOutEventId = eventId;
    }

    public void TrackSuppressed(int? areaId, int eventId)
    {
        lock (_gate) StateOf(areaId).Suppressed.Add(eventId);
    }

    public void RemoveSuppressed(int? areaId, int eventId)
    {
        lock (_gate)
        {
            if (_areas.TryGetValue(Key(areaId), out var state)) state.Suppressed.Remove(eventId);
        }
    }

    // 洪水结束后由调用方取走该区域仍被洪水抑制的报警并逐个解除。
    public IReadOnlyList<int> ReleaseSuppressed(int? areaId)
    {
        lock (_gate)
        {
            if (!_areas.TryGetValue(Key(areaId), out var state)) return [];
            var ids = state.Suppressed.ToList();
            state.Suppressed.Clear();
            return ids;
        }
    }

    public bool IsFloodSuppressed(int? areaId, int eventId)
    {
        lock (_gate) return _areas.TryGetValue(Key(areaId), out var state) && state.Suppressed.Contains(eventId);
    }

    private AreaState StateOf(int? areaId)
    {
        var key = Key(areaId);
        if (!_areas.TryGetValue(key, out var state)) _areas[key] = state = new AreaState();
        return state;
    }

    private static int Key(int? areaId) => areaId ?? 0;

    private static void Prune(AreaState state, long now)
    {
        while (state.Raises.Count > 0 && now - state.Raises.Peek() > WindowMs) state.Raises.Dequeue();
    }
}
