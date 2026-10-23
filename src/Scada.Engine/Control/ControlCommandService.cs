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
        CancellationToken cancellationToken = default)
    {
        if (!_config.TryGetTag(tagId, out var tag))
        {
            return new CommandExecutionResult(false, "测点不存在");
        }

        if (!_config.TryGetDevice(tag.DeviceId, out var device) || !device.Enabled)
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

        var normalized = tag.DataType == TagDataType.Bool ? (value != 0 ? 1 : 0) : value;
        var result = await _collector.WriteAsync(device.Id, tag, normalized, cancellationToken);
        return await LogAndReturnAsync(tag, normalized, userId, userName, result, cancellationToken);
    }

    private static bool Validate(Tag tag, double value, out string error)
    {
        error = string.Empty;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            error = "值无效";
            return false;
        }

        var (min, max) = DataTypeRange(tag.DataType);
        if (tag.MinValue is double configuredMin)
        {
            min = Math.Max(min, configuredMin);
        }

        if (tag.MaxValue is double configuredMax)
        {
            max = Math.Min(max, configuredMax);
        }

        if (value < min || value > max)
        {
            error = $"值超出允许范围（{min} ~ {max}）";
            return false;
        }

        return true;
    }

    private static (double Min, double Max) DataTypeRange(TagDataType dataType) => dataType switch
    {
        TagDataType.Bool => (0, 1),
        TagDataType.Int16 => (short.MinValue, short.MaxValue),
        TagDataType.UInt16 => (ushort.MinValue, ushort.MaxValue),
        TagDataType.Int32 => (int.MinValue, int.MaxValue),
        TagDataType.UInt32 => (uint.MinValue, uint.MaxValue),
        TagDataType.Float => (float.MinValue, float.MaxValue),
        _ => (double.MinValue, double.MaxValue)
    };

    private async Task<CommandExecutionResult> LogAndReturnAsync(
        Tag tag,
        double value,
        int? userId,
        string userName,
        CommandExecutionResult result,
        CancellationToken cancellationToken)
    {
        var oldValue = _cache.Get(tag.Id)?.Value;
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
                Result = result.Success ? $"成功，回读 {result.ReadBack:0.###}" : $"失败：{result.Message}"
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
