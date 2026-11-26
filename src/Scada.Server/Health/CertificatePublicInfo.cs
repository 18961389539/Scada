using System.Security.Cryptography.X509Certificates;

namespace Scada.Server.Health;

/// <summary>仅含公开信息，不含私钥、路径或口令。</summary>
public sealed record CertificatePublicInfo(string Role, string Subject, string Thumbprint, DateTime NotAfter)
{
    public static CertificatePublicInfo From(X509Certificate2 cert, string role) =>
        new(role, cert.Subject, cert.Thumbprint ?? "", cert.NotAfter);
}
