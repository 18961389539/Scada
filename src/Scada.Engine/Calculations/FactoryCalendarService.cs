using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;

namespace Scada.Engine.Calculations;

/// <summary>
/// 厂级工厂日历：读写配置库中的单行组态，并把有效值同步到内存中的 <see cref="ScadaSettings.ShiftTimeZone"/>，
/// 以便尚未改为直接依赖本服务的读者在刷新后仍能读到新时区。
/// </summary>
public sealed class FactoryCalendarService(
    IDbContextFactory<ConfigDbContext> factory,
    IOptions<ScadaSettings> options,
    ILogger<FactoryCalendarService>? logger = null) : IFactoryCalendar
{
    private readonly ILogger _logger = logger ?? NullLogger<FactoryCalendarService>.Instance;
    private readonly object _gate = new();
    private string? _configured;
    private TimeZoneInfo _zone = TimeZoneInfo.Local;
    private int _version;
    private bool _loaded;

    public string? ConfiguredId { get { EnsureLoaded(); lock (_gate) return _configured; } }
    public TimeZoneInfo Zone { get { EnsureLoaded(); lock (_gate) return _zone; } }
    public int Version { get { EnsureLoaded(); lock (_gate) return _version; } }
    public event Action? Changed;

    public void Refresh()
    {
        var (configured, zone) = Read();
        Apply(configured, zone, increment: true, notify: true);
    }

    internal void SeedFromSettingsIfEmpty()
    {
        try
        {
            using var db = factory.CreateDbContext();
            if (db.SiteOperationsSettings.Any())
            {
                Refresh();
                return;
            }
            var seed = string.IsNullOrWhiteSpace(options.Value.ShiftTimeZone) ? null : options.Value.ShiftTimeZone.Trim();
            db.SiteOperationsSettings.Add(new SiteOperationsSettings
            {
                Id = SiteOperationsSettings.SingletonId,
                ShiftTimeZone = seed,
                Revision = 1,
                UpdatedTs = ScadaTime.NowMs(),
                UpdatedBy = "system"
            });
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "初始化厂级工厂日历失败，暂时回退到站点配置或服务器本地时区");
        }
        Refresh();
    }

    public SiteOperationsSettings Set(string? timeZone, string? updatedBy)
    {
        EnsureLoaded();
        var trimmed = string.IsNullOrWhiteSpace(timeZone) ? null : timeZone.Trim();
        if (trimmed is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out _))
            throw new ArgumentException($"时区名称无法识别：{trimmed}");

        using var db = factory.CreateDbContext();
        var row = db.SiteOperationsSettings.SingleOrDefault(x => x.Id == SiteOperationsSettings.SingletonId);
        if (row is null)
        {
            row = new SiteOperationsSettings { Id = SiteOperationsSettings.SingletonId, Revision = 1 };
            db.SiteOperationsSettings.Add(row);
        }
        row.ShiftTimeZone = trimmed;
        row.Revision = row.Revision < 1 ? 2 : row.Revision + 1;
        row.UpdatedTs = ScadaTime.NowMs();
        row.UpdatedBy = updatedBy;
        db.SaveChanges();
        Apply(trimmed, CalcJobEngine.ResolveZone(trimmed), increment: true, notify: true);
        return row;
    }

    private void Apply(string? configured, TimeZoneInfo zone, bool increment, bool notify)
    {
        var changed = false;
        lock (_gate)
        {
            changed = _loaded && !string.Equals(_configured, configured, StringComparison.Ordinal);
            _configured = configured;
            _zone = zone;
            if (!_loaded || (increment && changed)) _version++;
            else if (!_loaded) _version = 1;
            _loaded = true;
            ApplySettings(configured);
        }
        if (notify && changed) Changed?.Invoke();
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        var (configured, zone) = Read();
        Apply(configured, zone, increment: false, notify: false);
    }

    private (string? Configured, TimeZoneInfo Zone) Read()
    {
        try
        {
            using var db = factory.CreateDbContext();
            var row = db.SiteOperationsSettings.AsNoTracking().SingleOrDefault(x => x.Id == SiteOperationsSettings.SingletonId);
            var configured = string.IsNullOrWhiteSpace(row?.ShiftTimeZone)
                ? (string.IsNullOrWhiteSpace(options.Value.ShiftTimeZone) ? null : options.Value.ShiftTimeZone.Trim())
                : row!.ShiftTimeZone!.Trim();
            return (configured, CalcJobEngine.ResolveZone(configured));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取厂级工厂日历失败，回退到站点配置或服务器本地时区");
            var fallback = string.IsNullOrWhiteSpace(options.Value.ShiftTimeZone) ? null : options.Value.ShiftTimeZone.Trim();
            return (fallback, CalcJobEngine.ResolveZone(fallback));
        }
    }

    private void ApplySettings(string? configured) => options.Value.ShiftTimeZone = configured;
}
