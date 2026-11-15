namespace Scada.Core.Entities;

/// <summary>
/// 审计发件箱。审计表在 runtime.db，配置写入在 config.db，两者无法同事务提交；
/// 直接把审计写到运行库会留下"配置已生效、审计没写进去"的窗口（进程崩溃、运行库被锁）。
/// 所以审计先与业务写入同一个事务落到本表，再由 <c>AuditOutboxDrainer</c> 搬到 OperationLog 并删除。
/// 同事务意味着：业务提交了，审计就一定在发件箱里，最多是晚一点到运行库。
/// </summary>
public sealed class AuditOutboxEntry
{
    public long Id { get; set; }
    public long CreatedTs { get; set; }
    /// <summary>OperationLog 的 JSON；发件箱不复制审计表的列，避免两处 schema 一起改。</summary>
    public string Payload { get; set; } = "";
    public int Attempts { get; set; }
    /// <summary>下次尝试时间（毫秒时间戳）。失败按指数退避，不影响已提交的业务写入。</summary>
    public long NextAttemptTs { get; set; }
    public string? LastError { get; set; }
}
