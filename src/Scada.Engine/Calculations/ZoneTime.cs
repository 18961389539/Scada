namespace Scada.Engine.Calculations;

// 工厂时区（ShiftTimeZone）上的时间对齐。整点时差与 UTC 整点重合；半点时区按本地钟点。
public static class ZoneTime
{
    private const long DayMs = 86_400_000L, HourMs = 3_600_000L;

    public static long FloorHour(long ts, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), zone);
        return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).ToUnixTimeMilliseconds();
    }

    public static long DayStart(long ts, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), zone).DateTime.Date;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    // 夏令时跳变日不足/超过 24 小时：以本地日期对齐为准，探针落回同一天时退化为固定 24 小时。
    public static long NextDay(long dayStart, TimeZoneInfo zone)
    {
        var n = DayStart(dayStart + DayMs + 2 * HourMs, zone);
        return n > dayStart ? n : dayStart + DayMs;
    }
}
