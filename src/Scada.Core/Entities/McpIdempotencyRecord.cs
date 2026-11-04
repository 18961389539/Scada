namespace Scada.Core.Entities;

/// <summary>
/// MCP 幂等键的持久化记录。进程内 single-flight 只能保证同一实例的并发调用执行一次，
/// 这里让已经执行过的 requestId 在重启、主备切换和多实例部署下仍然可重放：
/// 先用唯一索引占位（Pending），执行成功后写回结果（Completed），失败则删除占位允许重试。
/// </summary>
public class McpIdempotencyRecord
{
    public int Id { get; set; }

    /// <summary>工具名，与 requestId 一起构成幂等键。</summary>
    public string Tool { get; set; } = string.Empty;

    public Guid RequestId { get; set; }

    /// <summary>见 <see cref="McpIdempotencyStates"/>。</summary>
    public int Status { get; set; }

    /// <summary>首次执行结果的 JSON；仅在 Completed 时写入。</summary>
    public string? ResultJson { get; set; }

    public long CreatedTs { get; set; }

    public long? CompletedTs { get; set; }
}

public static class McpIdempotencyStates
{
    /// <summary>已占位，执行中。超过接管时限仍未完成视为进程崩溃留下的僵尸记录。</summary>
    public const int Pending = 0;

    /// <summary>执行完成，<see cref="McpIdempotencyRecord.ResultJson"/> 可直接重放。</summary>
    public const int Completed = 1;
}
