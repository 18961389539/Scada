namespace Scada.Server.Mcp;

public sealed class McpOptions
{
    public bool Enabled { get; set; }
    public bool AllowJwt { get; set; }
    public int RequestsPerMinute { get; set; } = 120;
}
