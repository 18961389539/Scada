using Scada.Core.Entities;

namespace Scada.Core.Interfaces;

public interface IAlarmNotifier
{
    Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default);
}

public interface IAlarmSink
{
    Task<int> RaiseDeviceOfflineAsync(Device device, string? reason, long ts, CancellationToken cancellationToken = default);
    Task RecoverDeviceOfflineAsync(int alarmEventId, long ts, CancellationToken cancellationToken = default);
}

// 通知通道凭据的加解密；服务端以 DataProtection 实现，密钥与设备口令同目录持久化。
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string protectedValue);
}
