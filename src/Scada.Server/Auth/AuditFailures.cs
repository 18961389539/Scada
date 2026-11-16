using Microsoft.Extensions.DependencyInjection;

namespace Scada.Server.Auth;

/// <summary>
/// 审计写入失败不能被静默吞掉：磁盘满或运行库只读时，外部 API / AI / 403 拒绝
/// 会「照常返回但一条记录都没有」，只有事后复盘才发现日志是空的。
/// 这里统一记错误日志并累计次数（可由健康/指标端点读取），响应结果保持不变。
/// </summary>
public static class AuditFailures
{
    private static long _count;

    /// <summary>进程启动以来审计写入失败的次数。</summary>
    public static long Count => Interlocked.Read(ref _count);

    public static void Report(IServiceProvider services, string source, Exception exception)
    {
        Interlocked.Increment(ref _count);
        services.GetService<ILoggerFactory>()
            ?.CreateLogger("Scada.Audit")
            .LogError(exception, "审计写入失败（{Source}）：本次操作不会出现在审计日志中", source);
    }
}
