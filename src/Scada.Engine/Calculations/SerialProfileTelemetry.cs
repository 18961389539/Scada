using System.Collections.Concurrent;
using Scada.Core.Models;
using Scada.Core.Scripting;

namespace Scada.Engine.Calculations;

public sealed record SerialProfileUsage(string Profile, bool Ok, long AtTs, double ElapsedMs, string? Error);

// 脚本 Worker 回传的串口调用结果，只保留每个 profile 的最近一次；进程重启后清空。
public sealed class SerialProfileTelemetry
{
    private readonly ConcurrentDictionary<string, SerialProfileUsage> _last = new(StringComparer.OrdinalIgnoreCase);

    public void Record(IEnumerable<ScriptSerialUse>? uses)
    {
        if (uses is null) return;
        var now = ScadaTime.NowMs();
        foreach (var use in uses)
        {
            var profile = use.Profile?.Trim() ?? "";
            if (profile.Length == 0) continue;
            _last[profile] = new SerialProfileUsage(profile, use.Ok, now, use.ElapsedMs, use.Error);
        }
    }

    public IReadOnlyDictionary<string, SerialProfileUsage> Snapshot() =>
        new Dictionary<string, SerialProfileUsage>(_last, StringComparer.OrdinalIgnoreCase);
}
