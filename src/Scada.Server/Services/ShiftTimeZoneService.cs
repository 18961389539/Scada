using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Engine.Calculations;
using Scada.Server.Startup;

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

public sealed class ShiftTimeZoneService(IOptions<ScadaSettings> options, IConfiguration configuration)
{
    public ShiftTimeZoneView Get()
    {
        var configured = string.IsNullOrWhiteSpace(options.Value.ShiftTimeZone) ? null : options.Value.ShiftTimeZone.Trim();
        var zone = CalcJobEngine.ResolveZone(configured);
        return new(configured, zone.Id, zone.DisplayName, configured is null, SitePath() is not null, Options());
    }

    public ShiftTimeZoneView Set(string? timeZone)
    {
        var trimmed = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
        if (trimmed is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out _))
            throw new ArgumentException($"时区名称无法识别：{trimmed}");
        options.Value.ShiftTimeZone = trimmed;
        var path = SitePath();
        var persisted = false;
        if (path is not null)
        {
            persisted = StartupPaths.TryWriteScadaProperty(path, "ShiftTimeZone", trimmed);
        }

        var view = Get();
        return view with { Persisted = persisted };
    }

    private string? SitePath()
    {
        var dataRoot = configuration["Scada:DataRoot"];
        if (string.IsNullOrWhiteSpace(dataRoot)) dataRoot = Path.Combine(AppContext.BaseDirectory, "data");
        return StartupPaths.ExistingSiteSettingsPath(configuration, dataRoot);
    }

    private static IReadOnlyList<ShiftTimeZoneOption> Options() =>
        TimeZoneInfo.GetSystemTimeZones()
            .OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ShiftTimeZoneOption(x.Id, x.DisplayName))
            .ToList();
}
