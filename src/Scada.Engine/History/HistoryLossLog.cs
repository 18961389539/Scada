using System.Text.Json;
using System.Text.Json.Serialization;
using Scada.Core.Models;

namespace Scada.Engine.History;

/// <summary>
/// 历史拒收与暂存丢弃窗口。默认持久化到历史目录下的 history-loss.jsonl，
/// 保留期与原始历史库一致；进程重启后仍能在趋势上标出这些缺口。
/// 缺失与坏质量来自历史库本身，不写入本日志，避免与曲线计算结果重复计数。
/// </summary>
public sealed class HistoryLossLog
{
    public const string FileName = "history-loss.jsonl";
    public const int Capacity = 512;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly List<HistoryGap> _windows = new();
    private readonly string? _path;
    private readonly Func<long>? _cutoffMs;

    public HistoryLossLog() : this(null, null) { }

    public HistoryLossLog(string? path, Func<long>? cutoffMs)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _cutoffMs = cutoffMs;
        if (_path is not null) LoadUnlocked();
    }

    public bool Persistent => _path is not null;
    public string? Path => _path;
    public int Count { get { lock (_gate) return _windows.Count; } }
    public long? OldestFrom
    {
        get { lock (_gate) return _windows.Count == 0 ? null : _windows.Min(x => x.From); }
    }

    public void Record(HistoryGapKind kind, long from, long to, int? tagId = null, string? detail = null)
    {
        if (kind is not HistoryGapKind.Rejected and not HistoryGapKind.SpoolDropped)
        {
            // 其他类型由查询结果直接计算，不进入持久化日志。
            AddMemory(new HistoryGap(tagId, Normalize(from, to).From, Normalize(from, to).To, kind, detail), persist: false);
            return;
        }
        var range = Normalize(from, to);
        AddMemory(new HistoryGap(tagId, range.From, range.To, kind, detail), persist: true);
    }

    public IReadOnlyList<HistoryGap> Overlapping(long from, long to, int? tagId = null)
    {
        lock (_gate)
        {
            PruneUnlocked();
            return _windows.Where(x => x.To >= from && x.From <= to
                                       && (tagId is null || x.TagId is null || x.TagId == tagId))
                           .ToArray();
        }
    }

    public IReadOnlyList<HistoryGap> Snapshot()
    {
        lock (_gate)
        {
            PruneUnlocked();
            return _windows.ToArray();
        }
    }

    public HistoryLossStatus Status()
    {
        lock (_gate)
        {
            PruneUnlocked();
            return new HistoryLossStatus(
                Persistent,
                _path,
                _windows.Count,
                _windows.Count == 0 ? null : _windows.Min(x => x.From),
                Persistent
                    ? "拒收与暂存丢弃窗口已按历史保留期持久化，重启后仍会在趋势上标出。缺失与坏质量来自历史库。"
                    : "拒收与暂存丢弃窗口仅记在本进程内存中，重启后不再显示。");
        }
    }


    /// <summary>
    /// 合并另一份缺口日志（例如接管时从对端拷贝），重叠窗口按已有规则去重合并。
    /// </summary>
    public int MergeFromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
        var added = 0;
        lock (_gate)
        {
            var before = _windows.Count;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var row = JsonSerializer.Deserialize<LossRow>(line, Json);
                    if (row is null) continue;
                    if (row.Kind is not HistoryGapKind.Rejected and not HistoryGapKind.SpoolDropped) continue;
                    var range = Normalize(row.From, row.To);
                    MergeUnlocked(new HistoryGap(row.TagId, range.From, range.To, row.Kind, row.Detail));
                    added++;
                }
            }
            catch (Exception)
            {
                // 损坏的对端日志不阻断接管，已解析的窗口仍然保留。
            }
            PruneUnlocked();
            PersistUnlocked();
            _ = before;
        }
        return added;
    }
    private static (long From, long To) Normalize(long from, long to)
    {
        if (to < from) (from, to) = (to, from);
        if (to == from) to = from + 1;
        return (from, to);
    }

    private void AddMemory(HistoryGap gap, bool persist)
    {
        lock (_gate)
        {
            PruneUnlocked();
            MergeUnlocked(gap);
            if (persist) PersistUnlocked();
        }
    }

    private void MergeUnlocked(HistoryGap gap)
    {
        for (var i = 0; i < _windows.Count; i++)
        {
            var existing = _windows[i];
            if (existing.Kind != gap.Kind || existing.TagId != gap.TagId) continue;
            // 相邻或重叠的同一类窗口合并，避免回放与重复记录在图上叠两层。
            if (gap.From > existing.To + 1 || gap.To + 1 < existing.From) continue;
            _windows[i] = existing with
            {
                From = Math.Min(existing.From, gap.From),
                To = Math.Max(existing.To, gap.To),
                Detail = gap.Detail ?? existing.Detail
            };
            return;
        }
        _windows.Add(gap);
        CapUnlocked();
    }

    private void PruneUnlocked()
    {
        if (_cutoffMs is null) return;
        var cutoff = _cutoffMs();
        _windows.RemoveAll(x => x.To < cutoff);
        CapUnlocked();
    }

    private void CapUnlocked()
    {
        while (_windows.Count > Capacity)
        {
            var oldest = _windows.MinBy(x => x.From) ?? _windows[0];
            _windows.Remove(oldest);
        }
    }

    private void LoadUnlocked()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = JsonSerializer.Deserialize<LossRow>(line, Json);
                if (row is null) continue;
                if (row.Kind is not HistoryGapKind.Rejected and not HistoryGapKind.SpoolDropped) continue;
                var range = Normalize(row.From, row.To);
                MergeUnlocked(new HistoryGap(row.TagId, range.From, range.To, row.Kind, row.Detail));
            }
        }
        catch (Exception)
        {
            // 损坏的日志不阻断启动；内存窗口以成功解析的行和本进程新记录为准。
        }
        PruneUnlocked();
    }

    private void PersistUnlocked()
    {
        if (_path is null) return;
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream))
        {
            foreach (var gap in _windows.Where(x => x.Kind is HistoryGapKind.Rejected or HistoryGapKind.SpoolDropped))
                writer.WriteLine(JsonSerializer.Serialize(new LossRow(gap.Kind, gap.TagId, gap.From, gap.To, gap.Detail), Json));
        }
        File.Move(temp, _path, true);
    }

    private sealed record LossRow(HistoryGapKind Kind, int? TagId, long From, long To, string? Detail);
}

public sealed record HistoryLossStatus(
    bool Persistent,
    string? Path,
    int Windows,
    long? OldestFrom,
    string Note);
