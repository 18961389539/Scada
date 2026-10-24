using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Control;

public sealed class ControlCommandService
{
    private readonly IConfigSnapshot _config;
    private readonly ICollectorControl _collector;
    private readonly IRealtimeCache _cache;
    private readonly IDbContextFactory<ConfigDbContext> _contextFactory;
    private readonly ILogger<ControlCommandService> _logger;

    public ControlCommandService(
        IConfigSnapshot config,
        ICollectorControl collector,
        IRealtimeCache cache,
        IDbContextFactory<ConfigDbContext> contextFactory,
        ILogger<ControlCommandService> logger)
    {
        _config = config;
        _collector = collector;
        _cache = cache;
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<CommandExecutionResult> ExecuteAsync(
        int tagId,
        double value,
        int? userId,
        string userName,
        CancellationToken cancellationToken = default,
        Guid? requestId = null)
    {
        if (!_config.TryGetTag(tagId, out var tag))
        {
            return new CommandExecutionResult(false, "测点不存在");
        }

        if (tag.Kind != TagKind.Physical) return await LogAndReturnAsync(tag, value, userId, userName, new(false, "计算测点只读，不能下发现场控制"), cancellationToken);
        if (tag.DeviceId is not int deviceId || !_config.TryGetDevice(deviceId, out var device) || !device.Enabled)
        {
            return new CommandExecutionResult(false, "设备不存在或未启用");
        }

        if (!tag.Writable)
        {
            return await LogAndReturnAsync(tag, value, userId, userName, new CommandExecutionResult(false, "测点不可写"), cancellationToken);
        }

        if (!Validate(tag, value, out var error))
        {
            return await LogAndReturnAsync(tag, value, userId, userName, new CommandExecutionResult(false, error), cancellationToken);
        }

        var oldValue = _cache.Get(tag.Id)?.Value;
        var normalized = value;
        var result = await _collector.WriteIdentifiedAsync(device.Id, tag, normalized, new(requestId, userId, userName), cancellationToken);
        return await LogAndReturnAsync(tag, normalized, userId, userName, result, CancellationToken.None, oldValue);
    }

    private static bool Validate(Tag tag, double value, out string error)
    {
        return CommandValuePolicy.TryGetRawValue(tag, value, out _, out error);
    }

    private async Task<CommandExecutionResult> LogAndReturnAsync(
        Tag tag,
        double value,
        int? userId,
        string userName,
        CommandExecutionResult result,
        CancellationToken cancellationToken,
        double? valueBeforeCommand = null)
    {
        var oldValue = valueBeforeCommand;
        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            db.OperationLogs.Add(new OperationLog
            {
                UserId = userId,
                UserName = userName,
                Ts = ScadaTime.NowMs(),
                Action = $"控制写入 {tag.Name}",
                TagId = tag.Id,
                OldValue = oldValue?.ToString("0.###"),
                NewValue = value.ToString("0.###"),
                Result = result.Outcome == GatewayCommandOutcome.Unknown ? $"结果未知：{result.Message}"
                    : result.Outcome == GatewayCommandOutcome.NotSent ? $"未下发：{result.Message}"
                    : result.Success ? $"成功，回读 {result.ReadBack:0.###}" : $"失败：{result.Message}"
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写操作日志失败");
        }

        return result;
    }
}
