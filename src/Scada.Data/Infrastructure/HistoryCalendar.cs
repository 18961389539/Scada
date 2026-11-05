namespace Scada.Data.Infrastructure;

/// <summary>
/// 原始库按班次时区切月，同时仍能读到按 UTC 切月写下的旧文件。
/// </summary>
public static class HistoryCalendar
{
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) { return TimeZoneInfo.Utc; }
    }

    public static (int Year, int Month) MonthOf(long timestamp, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(timestamp), zone);
        return (local.Year, local.Month);
    }

    public static (int Year, int Month) UtcMonthOf(long timestamp)
    {
        var date = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
        return (date.Year, date.Month);
    }

    /// <summary>写入用的月份：班次时区的日历月。</summary>
    public static (int Year, int Month) WriteMonth(long timestamp, TimeZoneInfo zone) => MonthOf(timestamp, zone);

    /// <summary>读取要打开的月份：时区月和 UTC 月的并集，避免升级后旧文件消失。</summary>
    public static IEnumerable<(int Year, int Month)> MonthsOverlapping(long from, long to, TimeZoneInfo zone)
    {
        var seen = new HashSet<(int Year, int Month)>();
        foreach (var month in Walk(from, to, static ts => UtcMonthOf(ts)))
            if (seen.Add(month)) yield return month;
        if (zone.Equals(TimeZoneInfo.Utc)) yield break;
        foreach (var month in Walk(from, to, ts => MonthOf(ts, zone)))
            if (seen.Add(month)) yield return month;
    }

    private static IEnumerable<(int Year, int Month)> Walk(long from, long to, Func<long, (int Year, int Month)> monthOf)
    {
        if (to < from) yield break;
        var cursor = monthOf(from);
        var end = monthOf(to);
        while (true)
        {
            yield return cursor;
            if (cursor == end) yield break;
            var next = new DateTime(cursor.Year, cursor.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
            cursor = (next.Year, next.Month);
        }
    }
}
