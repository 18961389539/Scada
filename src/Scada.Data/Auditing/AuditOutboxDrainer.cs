using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Models;

namespace Scada.Data.Auditing;

/// <summary>
/// 把配置库里的审计发件箱搬到运行库。三条约束：
/// 1. 只搬运、不阻塞业务：内联尝试给短超时，搬不动就留在发件箱等后台重试；
/// 2. 重复投递不会写出重复审计：OperationLog.SourceId 上有唯一索引，投递前先查已存在的 SourceId；
/// 3. 永远不抛：调用方是拦截器，审计失败不能回滚已提交的业务写入。
/// </summary>
    // SemaphoreSlim 只有用到 AvailableWaitHandle 才分配内核句柄；本类只用 WaitAsync/Release，
    // 没有需要释放的非托管资源，CA1001 在这里是误报。
    [SuppressMessage("Microsoft.Usage", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim 仅使用 WaitAsync/Release，不分配等待句柄")]
public sealed class AuditOutboxDrainer(
    IDbContextFactory<ConfigDbContext> configFactory,
    IDbContextFactory<RuntimeDbContext> runtimeFactory,
    ILogger<AuditOutboxDrainer> logger)
{
    public const int BatchSize = 200;
    // 内联尝试的命令超时：运行库被报警写入占住时，配置保存最多等这么久，剩下的交给后台。
    public const int InlineTimeoutSeconds = 2;
    private const int MaxBackoffSeconds = 3600;

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// 内联尝试：拿不到闸（已有 drain 在跑）就直接让后台接手。
    /// 只把行投到运行库，**绝不写配置库**——删发件箱行、写退避都要拿配置库写锁，
    /// 而调用方此刻常常还开着自己的事务（端点 BeginTransactionAsync 尚未 Commit），
    /// 另开一条连接去写会一直等到命令超时，把配置保存拖成 30 秒的 HTTP 超时。
    /// </summary>
    public async Task<bool> TryDrainAsync(int maxRows, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            return await DeliverOnlyAsync(maxRows, commandTimeoutSeconds, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> DeliverOnlyAsync(int maxRows, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await using var config = await configFactory.CreateDbContextAsync(cancellationToken);
            if (commandTimeoutSeconds > 0) config.Database.SetCommandTimeout(commandTimeoutSeconds);
            var rows = await config.Set<AuditOutboxEntry>().AsNoTracking()
                .Where(x => x.NextAttemptTs <= ScadaTime.NowMs())
                .OrderBy(x => x.Id).Take(maxRows)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) return true;
            var logs = Read(rows);
            if (logs.Count > 0) await DeliverAsync(logs, commandTimeoutSeconds, cancellationToken);
            // 行留着：后台 DrainAsync 会按 SourceId 查重后安全删掉，不会写出重复审计。
            return true;
        }
        catch (Exception ex)
        {
            // 运行库被锁是常态（报警风暴），内联失败只影响时效；后台会带退避重试。
            // 这里连退避都不写：写退避同样要抢配置库写锁。
            logger.LogDebug(ex, "审计内联投递未完成，等待后台补投");
            return false;
        }
    }

    /// <summary>后台调用：允许排队等待闸，投递成功后才删行。</summary>
    public async Task<bool> DrainAsync(int maxRows, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await DrainCoreAsync(maxRows, commandTimeoutSeconds, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> DrainCoreAsync(int maxRows, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            await using var config = await configFactory.CreateDbContextAsync(cancellationToken);
            if (commandTimeoutSeconds > 0) config.Database.SetCommandTimeout(commandTimeoutSeconds);
            var now = ScadaTime.NowMs();
            var rows = await config.Set<AuditOutboxEntry>()
                .Where(x => x.NextAttemptTs <= now)
                .OrderBy(x => x.Id)
                .Take(maxRows)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0) return true;

            var logs = Read(rows);
            if (logs.Count > 0) await DeliverAsync(logs, commandTimeoutSeconds, cancellationToken);

            config.Set<AuditOutboxEntry>().RemoveRange(rows);
            await config.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "审计发件箱投递失败，已留待重试");
            await BackoffAsync(maxRows, ex, cancellationToken);
            return false;
        }
    }

    private async Task DeliverAsync(List<OperationLog> logs, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
        if (commandTimeoutSeconds > 0) runtime.Database.SetCommandTimeout(commandTimeoutSeconds);

        // 崩溃后重试时这些行可能已经写过：先按 SourceId 查重，避免重复审计。
        var ids = logs.Where(x => x.SourceId is not null).Select(x => x.SourceId!.Value).ToArray();
        var seen = ids.Length == 0
            ? []
            : await runtime.OperationLogs.AsNoTracking()
                .Where(x => x.SourceId != null && ids.Contains(x.SourceId.Value))
                .Select(x => x.SourceId!.Value)
                .ToListAsync(cancellationToken);
        var delivered = seen.Count == 0 ? logs : logs.Where(x => x.SourceId is null || !seen.Contains(x.SourceId.Value)).ToList();
        if (delivered.Count == 0) return;

        runtime.OperationLogs.AddRange(delivered);
        await runtime.SaveChangesAsync(cancellationToken);
    }

    private async Task BackoffAsync(int maxRows, Exception error, CancellationToken cancellationToken)
    {
        try
        {
            await using var config = await configFactory.CreateDbContextAsync(cancellationToken);
            config.Database.SetCommandTimeout(5);
            var now = ScadaTime.NowMs();
            var rows = await config.Set<AuditOutboxEntry>()
                .Where(x => x.NextAttemptTs <= now)
                .OrderBy(x => x.Id)
                .Take(maxRows)
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.Attempts++;
                var delay = Math.Min(MaxBackoffSeconds, 5 * (1 << Math.Min(row.Attempts, 10)));
                row.NextAttemptTs = now + delay * 1000L;
                row.LastError = Truncate(error.Message, 500);
            }
            if (rows.Count > 0) await config.SaveChangesAsync(cancellationToken);
        }
        catch (Exception backoffError)
        {
            // 连退避都写不进去（配置库被锁），下一轮后台任务会再试；这里只记录。
            logger.LogDebug(backoffError, "审计发件箱退避写入失败");
        }
    }

    private static List<OperationLog> Read(IEnumerable<AuditOutboxEntry> rows)
    {
        var logs = new List<OperationLog>();
        foreach (var row in rows)
        {
            var log = Deserialize(row);
            if (log is null) continue;
            // 幂等键就在这里赋值：发件箱行号在写入时还不知道（自增主键），
            // 但在投递时是稳定的，重复投递会被 OperationLog.SourceId 上的唯一索引挡住。
            log.SourceId = row.Id;
            logs.Add(log);
        }
        return logs;
    }

    private static OperationLog? Deserialize(AuditOutboxEntry row)
    {
        try
        {
            return JsonSerializer.Deserialize<OperationLog>(row.Payload, PayloadJson);
        }
        catch (JsonException)
        {
            return null; // 坏行直接丢弃，不能让它永远卡住发件箱。
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
