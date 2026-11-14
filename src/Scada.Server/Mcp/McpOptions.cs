namespace Scada.Server.Mcp;

public sealed class McpOptions
{
    public bool Enabled { get; set; } = true;
    public int RequestsPerMinute { get; set; } = 120;

    /// <summary>
    /// 允许访问 MCP 端点的来源网段（CIDR 或单个地址）。为空表示只允许本机回环；
    /// 只有显式写 "*" 才表示不限制来源，且仅应在明确可控的网络上使用。
    /// </summary>
    public string[] AllowedCidrs { get; set; } = [];

    /// <summary>
    /// 允许注册会改变现场状态的工具（写测点、顺控、配方下发、挂牌、设备重连）。
    /// 默认关闭：匿名端点叠加写能力，是交付现场最容易出事的一种组合。
    /// 打开时必须同时指定 <see cref="ControlUser"/>，写操作按该用户的区域权限执行。
    /// </summary>
    public bool AllowControlWrites { get; set; }

    /// <summary>
    /// 控制写所代表的站内用户名。只使用该用户的区域权限，审计身份仍是 mcp:lan。
    /// <see cref="AllowControlWrites"/> 为 true 时不能为空。
    /// </summary>
    public string? ControlUser { get; set; }

    /// <summary>
    /// 允许注册改动配置的工具（画面、布局、对象模板、主数据、报表模板）。
    /// 默认关闭，与 <see cref="AllowControlWrites"/> 分开，便于只开放其中一类。
    /// </summary>
    public bool AllowConfigurationWrites { get; set; }

    /// <summary>
    /// 审计落库的最长等待（毫秒）。0 表示只入队不等待：数据库抖动时不会拖慢工具响应，
    /// 代价是客户端收到响应时审计可能尚未落库。
    /// </summary>
    public int AuditFlushTimeoutMs { get; set; } = 5000;
}
