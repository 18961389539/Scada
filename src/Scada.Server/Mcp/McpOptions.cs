namespace Scada.Server.Mcp;

public sealed class McpOptions
{
    public bool Enabled { get; set; } = true;
    public int RequestsPerMinute { get; set; } = 120;
}
