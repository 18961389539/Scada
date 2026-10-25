namespace Scada.Core.Options;

public sealed class ExternalIntegrationOptions
{
    public ExternalRestOptions Rest { get; set; } = new();
    public ExternalTcpOptions Tcp { get; set; } = new();
    public ExternalOpcUaOptions OpcUa { get; set; } = new();
}

public sealed class ExternalRestOptions
{
    public bool Enabled { get; set; } = true;
    // 额外 CORS 来源（与内部 web 策略分离）；空 = 不额外放行。
    public string[] CorsOrigins { get; set; } = [];
    public int DefaultRateLimitPerMinute { get; set; } = 120;
    // 是否允许用登录 JWT 访问 /api/v1（权限按用户区域授权映射到 scopes）。
    public bool AllowJwt { get; set; } = true;
}

public sealed class ExternalTcpOptions
{
    public bool Enabled { get; set; }
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 7500;
    public bool Tls { get; set; }
    public string? TlsCertPath { get; set; }
    public string? TlsCertPassword { get; set; }
    public int MaxConnections { get; set; } = 64;
    public string[] IpAllowlist { get; set; } = [];
    public int IdleTimeoutSec { get; set; } = 120;
    public int MaxFrameBytes { get; set; } = 256 * 1024;
}

public sealed class ExternalOpcUaOptions
{
    public bool Enabled { get; set; }
    public string EndpointUrl { get; set; } = "opc.tcp://0.0.0.0:4840/Scada";
    // None / Sign / SignAndEncrypt（可多选，逗号分隔）。
    public string SecurityModes { get; set; } = "None,Sign,SignAndEncrypt";
    public bool AllowAnonymous { get; set; }
    public bool AllowUsernamePassword { get; set; } = true;
    public bool AllowCertificateUser { get; set; }
    // 空 = 使用 keys/opcua-server.pfx，不存在则自动生成自签名。
    public string? ServerCertPath { get; set; }
    public string? ServerCertPassword { get; set; }
    public int MaxSessionCount { get; set; } = 50;
    public int PublishingIntervalMs { get; set; } = 500;
}
