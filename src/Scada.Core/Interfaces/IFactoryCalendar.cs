namespace Scada.Core.Interfaces;

/// <summary>
/// 厂级工厂日历（班次时区）。写入配置库后通知订阅者失效并重读。
/// 未配置时与既有行为一致：按服务器本地时区切日，历史小时桶仍按 UTC 整点。
/// </summary>
public interface IFactoryCalendar
{
    string? ConfiguredId { get; }
    TimeZoneInfo Zone { get; }
    int Version { get; }
    event Action? Changed;
    void Refresh();
}
