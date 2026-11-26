using Scada.Server.Health;

namespace Scada.Server.Tests;

public class CertificateExpiryHealthTests
{
    [Fact]
    public void ExpiredAndExpiringCertificates_ProduceWarningsWithoutSecrets()
    {
        var now = new DateTime(2026, 11, 26, 12, 0, 0);
        var expired = new CertificatePublicInfo("report-signing", "CN=Report", "ABC123", now.AddDays(-1));
        var expiring = new CertificatePublicInfo("external-tcp-tls", "CN=Tcp", "DEF456", now.AddDays(10));
        var ok = new CertificatePublicInfo("report-signing", "CN=Ok", "EEE777", now.AddDays(90));
        var warnings = CertificateExpiryHealth.Evaluate(now, [expired, expiring, ok]);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Code == "report_signing_cert_expired" && w.Message.Contains("ABC123") && !w.Message.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Code == "external_tcp_tls_cert_expiring" && w.Message.Contains("DEF456"));
        Assert.DoesNotContain(warnings, w => w.Message.Contains("EEE777"));
    }
}
