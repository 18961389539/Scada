namespace Scada.Core.Opc;

public sealed record OpcCertificateInfo(string Sha256, string Thumbprint, string Subject, string Issuer, DateTime NotBefore, DateTime NotAfter, string Store);

public interface IOpcCertificateService
{
    Task<IReadOnlyList<OpcCertificateInfo>> ListAsync(CancellationToken ct);
    Task SetTrustAsync(string sha256, bool trust, CancellationToken ct);
    Task<byte[]> PublicCertificateAsync(string sha256, CancellationToken ct);
}
