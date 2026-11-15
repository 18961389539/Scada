using Scada.Core.Models;
using Scada.Engine.Health;
using Scada.Engine.Maintenance;

namespace Scada.Server.Services;

/// <summary>
/// 运行端健康上报：保存最近一段时间的画面加载/数据读取异常，供 /health 与运维页面查看。
/// 同一画面同一类型 30 秒内只刷新一次，避免客户端重试把日志和告警刷爆。
/// </summary>
public sealed class ScreenRuntimeHealth : IHealthContributor
{
    public static readonly HashSet<string> Kinds =
    [
        "loadFailed", "requiresRepublish", "valueReadFailed", "tagCatalogFailed", "alarmLoadFailed",
        "historyLoadFailed", "deviceCatalogFailed", "connectionLost", "clientError"
    ];

    private sealed class Entry
    {
        public int ScreenId { get; init; }
        public string ScreenName { get; set; } = "";
        public string Kind { get; init; } = "";
        public string Message { get; set; } = "";
        public long LastTs { get; set; }
        public int Count { get; set; }
    }

    private static readonly long WindowMs = (long)TimeSpan.FromMinutes(15).TotalMilliseconds;
    private static readonly long ThrottleMs = (long)TimeSpan.FromSeconds(30).TotalMilliseconds;
    private const int MaxEntries = 200;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];

    /// <summary>返回是否产生了一条新的（非节流）健康记录，调用方据此决定是否写审计。</summary>
    public bool Report(int screenId, string screenName, string kind, string message, long now)
    {
        lock (_gate)
        {
            var key = $"{screenId}:{kind}";
            if (_entries.TryGetValue(key, out var entry))
            {
                entry.Count++;
                entry.Message = message;
                entry.ScreenName = screenName;
                if (now - entry.LastTs < ThrottleMs) return false;
                entry.LastTs = now;
                return true;
            }
            _entries[key] = new Entry { ScreenId = screenId, ScreenName = screenName, Kind = kind, Message = message, LastTs = now, Count = 1 };
            if (_entries.Count > MaxEntries)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.LastTs).First().Key;
                _entries.Remove(oldest);
            }
            return true;
        }
    }

    public IEnumerable<SystemWarning> HealthWarnings()
    {
        var cutoff = ScadaTime.NowMs() - WindowMs;
        lock (_gate)
        {
            return _entries.Values
                .Where(entry => entry.LastTs >= cutoff)
                .OrderByDescending(entry => entry.LastTs)
                .Take(20)
                .Select(entry => new SystemWarning("screen_runtime_error",
                    $"画面运行异常：{entry.ScreenName}（{KindLabel(entry.Kind)}，{entry.Count} 次）：{entry.Message}"))
                .ToList();
        }
    }

    private static string KindLabel(string kind) => kind switch
    {
        "loadFailed" => "画面加载失败",
        "requiresRepublish" => "需重新发布",
        "valueReadFailed" => "实时值读取失败",
        "tagCatalogFailed" => "测点目录不可用",
        "alarmLoadFailed" => "报警读取失败",
        "historyLoadFailed" => "历史读取失败",
        "deviceCatalogFailed" => "设备目录不可用",
        "connectionLost" => "实时连接中断",
        _ => "客户端异常"
    };
}
