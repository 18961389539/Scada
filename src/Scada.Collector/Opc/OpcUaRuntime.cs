using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Scada.Core.Entities;
using Scada.Core.Interfaces;

namespace Scada.Collector.Opc;

public sealed class OpcUaRuntime(OpcUaClientOptions options, ITelemetryContext telemetry, IDeviceCredentials credentials)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ISession, byte> _sessions = new();
    public async Task<ApplicationConfiguration> ConfigurationAsync(CancellationToken ct) => options.ConfigurationProvider is not null
        ? await options.ConfigurationProvider.GetAsync(ct) : options.Configuration ?? throw new InvalidOperationException("OPC UA 客户端尚未初始化");

    public async Task<ISession> ConnectAsync(Device device, CancellationToken ct)
    {
        var config = await ConfigurationAsync(ct);
        using var discovery = await DiscoveryClient.CreateAsync(config, new Uri(device.OpcEndpointUrl), DiagnosticsMasks.None, ct);
        var endpoints = await discovery.GetEndpointsAsync(default, ct);
        var mode = device.OpcSecurityMode switch { Core.Enums.OpcSecurityMode.None => MessageSecurityMode.None,
            Core.Enums.OpcSecurityMode.Sign => MessageSecurityMode.Sign, _ => MessageSecurityMode.SignAndEncrypt };
        var policy = device.OpcSecurityPolicy switch { Core.Enums.OpcSecurityPolicy.None => SecurityPolicies.None,
            Core.Enums.OpcSecurityPolicy.Aes128Sha256RsaOaep => SecurityPolicies.Aes128_Sha256_RsaOaep,
            Core.Enums.OpcSecurityPolicy.Aes256Sha256RsaPss => SecurityPolicies.Aes256_Sha256_RsaPss, _ => SecurityPolicies.Basic256Sha256 };
        var endpoint = endpoints.Span.ToArray().FirstOrDefault(x => x.SecurityMode == mode && x.SecurityPolicyUri == policy)
            ?? throw new IOException("服务器未提供所选安全模式及策略，连接不会自动降级");
        endpoint.EndpointUrl = device.OpcEndpointUrl;
        var endpointConfig = EndpointConfiguration.Create(config); endpointConfig.OperationTimeout = device.TimeoutMs;
        var configured = new ConfiguredEndpoint(null, endpoint, endpointConfig);
        IUserIdentity identity = new UserIdentity();
        byte[]? secret = null;
        try
        {
            if (!string.IsNullOrEmpty(device.OpcUserName))
            {
                secret = Encoding.UTF8.GetBytes(device.OpcPasswordProtected is null ? "" : credentials.Unprotect(device.OpcPasswordProtected));
                identity = new UserIdentity(device.OpcUserName, secret);
            }
            var session = await new DefaultSessionFactory(telemetry).CreateAsync(config, configured, false, true,
                $"SCADA-{device.Id}-{device.Name}", 60000, identity, default, ct);
            _sessions.TryAdd(session, 0); return session;
        }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); }
    }
    public void Release(ISession session) { _sessions.TryRemove(session, out _); session.Dispose(); }
    public void CloseAll() { foreach (var session in _sessions.Keys) Release(session); }
}
