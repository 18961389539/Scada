using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Scada.Collector.Opc;

namespace Scada.Server.Services;

public sealed record OpcCertificateInfo(string Sha256, string Thumbprint, string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Store);

public sealed class OpcCertificateService(OpcUaRuntime runtime)
{
    public async Task<IReadOnlyList<OpcCertificateInfo>> ListAsync(CancellationToken ct)
    {
        var config = await runtime.ConfigurationAsync(ct);
        var paths = new[] { ("trusted", config.SecurityConfiguration.TrustedPeerCertificates?.StorePath), ("rejected", config.SecurityConfiguration.RejectedCertificateStore?.StorePath), ("own", config.SecurityConfiguration.ApplicationCertificate?.StorePath) };
        var result = new List<OpcCertificateInfo>();
        foreach (var (name, root) in paths)
        foreach (var file in Files(root))
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            using var cert = X509CertificateLoader.LoadCertificate(bytes);
            result.Add(new(Convert.ToHexString(SHA256.HashData(bytes)), cert.Thumbprint, cert.Subject, cert.Issuer, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), name));
        }
        return result;
    }

    public async Task SetTrustAsync(string sha256, bool trust, CancellationToken ct)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("证书 SHA-256 指纹无效");
        var config = await runtime.ConfigurationAsync(ct);
        var trusted = config.SecurityConfiguration.TrustedPeerCertificates?.StorePath ?? throw new InvalidOperationException("OPC UA 信任库未配置");
        if (trust)
        {
            byte[]? bytes = null;
            foreach (var file in Files(config.SecurityConfiguration.RejectedCertificateStore?.StorePath))
            {
                var candidate = await File.ReadAllBytesAsync(file, ct);
                if (string.Equals(Convert.ToHexString(SHA256.HashData(candidate)), sha256, StringComparison.OrdinalIgnoreCase)) { bytes = candidate; break; }
            }
            if (bytes is null) throw new KeyNotFoundException("待信任证书不存在，请先发起连接");
            using var cert = X509CertificateLoader.LoadCertificate(bytes);
            if (cert.NotBefore.ToUniversalTime() > DateTime.UtcNow || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow) throw new ArgumentException("证书尚未生效或已过期");
            var directory = Path.GetFullPath(Path.Combine(trusted, "certs")); Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, sha256.ToUpperInvariant() + ".der"), bytes, ct);
        }
        else
        {
            foreach (var file in Files(trusted))
                if (string.Equals(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(file, ct))), sha256, StringComparison.OrdinalIgnoreCase)) File.Delete(file);
        }
        // Established sessions must be closed as well as changing the trust store.
        runtime.CloseAll();
    }

    public async Task<byte[]> PublicCertificateAsync(string sha256, CancellationToken ct)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new ArgumentException("证书指纹无效");
        var config = await runtime.ConfigurationAsync(ct);
        foreach (var file in Files(config.SecurityConfiguration.ApplicationCertificate?.StorePath))
        {
            var bytes = await File.ReadAllBytesAsync(file, ct);
            if (string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), sha256, StringComparison.OrdinalIgnoreCase)) return bytes;
        }
        throw new KeyNotFoundException("客户端公钥证书不存在");
    }
    private static IEnumerable<string> Files(string? root)
    { if (string.IsNullOrWhiteSpace(root)) return []; var certs = Path.GetFullPath(Path.Combine(root, "certs")); return Directory.Exists(certs) ? Directory.EnumerateFiles(certs, "*.der").Take(1000) : []; }
}
