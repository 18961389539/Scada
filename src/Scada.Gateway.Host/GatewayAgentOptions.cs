namespace Scada.Gateway.Hosting;

public sealed class GatewayAgentOptions
{
    public string ServerUrl { get; set; } = "";
    public int GatewayId { get; set; }
    public string Token { get; set; } = "";
    public string DataRoot { get; set; } = "data/gateway";
    public string ServiceName { get; set; } = "ScadaGateway";
    public bool AllowLoopbackHttp { get; set; }
    public int HttpTimeoutMs { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 5000;
    public int ConfigurationPollMs { get; set; } = 5000;
    public int RetryDelayMs { get; set; } = 1000;
    public long MaxPendingBytes { get; set; } = 256L * 1024 * 1024;
    public int CommandTimeoutMs { get; set; } = 5000;
    public Uri Server { get; private set; } = null!;
    public void Validate()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var server)) throw new ArgumentException("请配置 GatewayAgent:ServerUrl");
        Server = GatewayHttp.Server(server, AllowLoopbackHttp); ServerUrl = Server.AbsoluteUri;
        if (GatewayId < 1 || string.IsNullOrEmpty(Token) || Token.Length is < 40 or > 256 || Token.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new ArgumentException("请配置有效的网关编号与密钥");
        if (string.IsNullOrEmpty(ServiceName) || ServiceName.Length > 80 || !char.IsAsciiLetter(ServiceName[0]) || ServiceName.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new ArgumentException("网关服务名称无效");
        if (string.IsNullOrWhiteSpace(DataRoot) || MaxPendingBytes < 1024 || HttpTimeoutMs is < 100 or > 60000 || HeartbeatIntervalMs is < 500 or > 10000
            || ConfigurationPollMs is < 500 or > 60000 || RetryDelayMs is < 100 or > 30000 || CommandTimeoutMs is < 100 or > 120000)
            throw new ArgumentException("网关目录、容量或时间参数无效");
        DataRoot = Path.GetFullPath(DataRoot, AppContext.BaseDirectory);
    }
}
