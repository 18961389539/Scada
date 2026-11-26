using Microsoft.Extensions.Hosting;
using Scada.Engine.Health;
using Scada.Engine.Maintenance;
using Scada.Server.External.Tcp;
using Scada.Server.Reports;

namespace Scada.Server.Health;

/// <summary>
/// 报表签名证书与已启用的对外 TCP TLS 证书过期前预警。
/// 健康检查只读缓存；证书文件只在后台刷新时读取，不在 /health 热路径做 IO，也不返回私钥。
/// </summary>
public sealed class CertificateExpiryHealth(
    ReportSigner signer,
    ExternalTcpHost tcp) : IHealthContributor, IHostedService, IDisposable
{
    public const int WarnDays = 30;
    private volatile IReadOnlyList<SystemWarning> _warnings = [];
    private Timer? _timer;

    public IEnumerable<SystemWarning> HealthWarnings() => _warnings;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Refresh();
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromHours(1), TimeSpan.FromHours(1));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    internal void Refresh()
    {
        var certs = new List<CertificatePublicInfo>();
        if (signer.TryGetExistingPublicInfo() is { } report)
            certs.Add(report);
        if (tcp.TryGetTlsPublicInfo() is { } tls)
            certs.Add(tls);
        _warnings = Evaluate(DateTime.Now, certs, WarnDays);
    }

    public static IReadOnlyList<SystemWarning> Evaluate(DateTime now, IEnumerable<CertificatePublicInfo> certs, int warnDays = WarnDays)
    {
        var list = new List<SystemWarning>();
        var until = now.AddDays(warnDays);
        foreach (var cert in certs)
        {
            var (codeBase, title) = cert.Role switch
            {
                "report-signing" => ("report_signing_cert", "报表签名证书"),
                "external-tcp-tls" => ("external_tcp_tls_cert", "对外 TCP TLS 证书"),
                _ => (cert.Role, cert.Role)
            };
            var when = cert.NotAfter.ToString("yyyy-MM-dd HH:mm");
            var detail = $"{title}（主题 {cert.Subject}，指纹 {cert.Thumbprint}）";
            if (cert.NotAfter <= now)
                list.Add(new SystemWarning($"{codeBase}_expired", $"{detail}已于 {when} 过期，请更换"));
            else if (cert.NotAfter <= until)
                list.Add(new SystemWarning($"{codeBase}_expiring", $"{detail}将于 {when} 过期，请提前更换"));
        }
        return list;
    }
}
