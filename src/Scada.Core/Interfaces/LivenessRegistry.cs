using System.Collections.Concurrent;

namespace Scada.Core.Interfaces;

// 关键后台循环的心跳登记，供看门狗判断“进程活着但卡死”。
public sealed class LivenessRegistry
{
    private readonly ConcurrentDictionary<string, long> _beats = new(StringComparer.Ordinal);
    public void Beat(string loop) => _beats[loop] = Environment.TickCount64;
    public IReadOnlyList<(string Name, TimeSpan Age)> Ages()
    {
        var now = Environment.TickCount64;
        return _beats.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (x.Key, TimeSpan.FromMilliseconds(now - x.Value))).ToList();
    }
}
