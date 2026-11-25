using Scada.Engine.Calculations;

namespace Scada.Server.Services;

public sealed record ShiftTimeZoneView(
    string? Configured,
    string EffectiveId,
    string EffectiveDisplay,
    bool UsesServerLocal,
    bool Persisted,
    IReadOnlyList<ShiftTimeZoneOption> Options);

public sealed record ShiftTimeZoneOption(string Id, string Display);

public sealed record ShiftTimeZoneRequest(string? TimeZone);

public sealed class ShiftTimeZoneService(FactoryCalendarService calendar)
{
    public ShiftTimeZoneView Get()
    {
        var configured = calendar.ConfiguredId;
        var zone = calendar.Zone;
        return new(configured, zone.Id, zone.DisplayName, configured is null, true, Options());
    }

    public ShiftTimeZoneView Set(string? timeZone, string? updatedBy = null)
    {
        calendar.Set(timeZone, updatedBy);
        return Get();
    }

    private static IReadOnlyList<ShiftTimeZoneOption> Options() =>
        TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ShiftTimeZoneOption(x.Id, x.DisplayName))
            .ToList();
}
