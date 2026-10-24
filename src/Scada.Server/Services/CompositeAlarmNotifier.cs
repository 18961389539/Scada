using Scada.Core.Entities;
using Scada.Core.Interfaces;

namespace Scada.Server.Services;

// 一个报警要同时走站内推送和外发通道；任一通道异常都不能影响另一个，更不能打断采集。
public sealed class CompositeAlarmNotifier : IAlarmNotifier
{
    private readonly IReadOnlyList<IAlarmNotifier> _notifiers;
    private readonly ILogger<CompositeAlarmNotifier> _logger;

    public CompositeAlarmNotifier(IEnumerable<IAlarmNotifier> notifiers, ILogger<CompositeAlarmNotifier> logger)
    {
        _notifiers = notifiers.ToList();
        _logger = logger;
    }

    public async Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
    {
        foreach (var notifier in _notifiers)
        {
            try
            {
                await notifier.PublishAlarmAsync(alarm, action, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "报警通知通道 {Notifier} 发送失败，其余通道继续", notifier.GetType().Name);
            }
        }
    }
}
