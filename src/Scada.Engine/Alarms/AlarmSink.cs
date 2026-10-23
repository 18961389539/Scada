using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Data;

namespace Scada.Engine.Alarms;

public sealed class AlarmSink : IAlarmSink
{
    private readonly IDbContextFactory<ConfigDbContext> _contextFactory;
    private readonly IAlarmNotifier _notifier;
    private readonly ILogger<AlarmSink> _logger;

    public AlarmSink(
        IDbContextFactory<ConfigDbContext> contextFactory,
        IAlarmNotifier notifier,
        ILogger<AlarmSink> logger)
    {
        _contextFactory = contextFactory;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<int> RaiseDeviceOfflineAsync(
        Device device,
        string message,
        long ts,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var alarm = new AlarmEvent
        {
            DeviceId = device.Id,
            Level = AlarmLevel.Warning,
            Value = 0,
            StartTs = ts,
            Message = message
        };
        db.AlarmEvents.Add(alarm);
        await db.SaveChangesAsync(cancellationToken);
        await NotifySafeAsync(alarm, "raise", cancellationToken);
        return alarm.Id;
    }

    public async Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await db.AlarmEvents.FirstOrDefaultAsync(x => x.Id == alarmEventId, cancellationToken);
        if (alarm is null || alarm.EndTs is not null)
        {
            return;
        }

        alarm.EndTs = ts;
        await db.SaveChangesAsync(cancellationToken);
        await NotifySafeAsync(alarm, "recover", cancellationToken);
    }

    private async Task NotifySafeAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken)
    {
        try
        {
            await _notifier.PublishAlarmAsync(alarm, action, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "设备离线报警推送失败");
        }
    }
}

public sealed class NullAlarmNotifier : IAlarmNotifier
{
    public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
