namespace Scada.Server.Ai;

/// <summary>
/// 面向大模型的只读 API 配置。启用后额外开放 /api/ai/* 一组只读端点，
/// 供 Function Calling 直接调用；与 MCP 并行，二者共用同一套数据与权限模型。
/// </summary>
public sealed class AiOptions
{
    /// <summary>是否开放 /api/ai/* 只读端点。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>单个 API 密钥每分钟可调用次数（0 表示沿用密钥自身的限流值）。</summary>
    public int RequestsPerMinute { get; set; }

    /// <summary>一次历史/趋势查询允许的最大跨度（天）。</summary>
    public int MaxRangeDays { get; set; } = 31;

    /// <summary>一次趋势查询允许返回的最大点数（防止把上下文撑爆）。</summary>
    public int MaxPoints { get; set; } = 1000;

    /// <summary>一次报警查询允许返回的最大条数。</summary>
    public int MaxAlarmRows { get; set; } = 200;

    /// <summary>是否在响应里附带只读工具清单（/api/ai/tools）。</summary>
    public bool ExposeToolManifest { get; set; } = true;
}
