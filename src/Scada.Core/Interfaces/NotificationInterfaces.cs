using Scada.Core.Entities;

namespace Scada.Core.Interfaces;

public interface IAlarmNotifier
{
    Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default);
}

public interface IAlarmSink
{
    Task<int> RaiseDeviceOfflineAsync(Device device, string message, long ts, CancellationToken cancellationToken = default);
    Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default);
}
