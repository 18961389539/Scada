namespace Scada.Engine.Calculations;

// 工厂时区（ShiftTimeZone）上的时间对齐。整点时差与 UTC 整点重合；半点时区按本地钟点。
public static class ZoneTime
{
    public static long FloorHour(long ts, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ts), zone);
        return new DateTimeOffset(local.Year, local.Month, local.Day, local.Hour, 0, 0, local.Offset).ToUnixTimeMilliseconds();
    }
}
