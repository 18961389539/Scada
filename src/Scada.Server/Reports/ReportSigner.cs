using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Scada.Core.Options;

namespace Scada.Server.Reports;

public sealed record SignatureCheck(bool Present, bool Valid, string? Error, string? Subject, string? Thumbprint, long? SigningTs, bool CurrentCertificate, bool ChainTrusted, bool SelfSigned);

// 报表签名：服务器证书对归档文件做 PKCS#7/CMS 分离签名（SHA-256，含签名时间），验证时检查签名、内容与签名证书指纹。
// 证书来自 Scada:ReportSigningCertPath（PFX），未配置时使用数据目录 keys/report-signing.pfx，不存在则生成自签名证书。
public sealed class ReportSigner
{
    public const string DefaultFileName = "report-signing.pfx";
    private readonly ScadaSettings _settings;
    private readonly ILogger<ReportSigner> _logger;
    private readonly Lazy<X509Certificate2> _cert;

    public ReportSigner(IOptions<ScadaSettings> options, ILogger<ReportSigner> logger)
    {
        _settings = options.Value; _logger = logger;
        _cert = new Lazy<X509Certificate2>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool Enabled => _settings.ReportSigningEnabled;
    public string KeysDirectory => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_settings.ConfigDbPath))!, "keys");
    public string CertificatePath => string.IsNullOrWhiteSpace(_settings.ReportSigningCertPath) ? Path.Combine(KeysDirectory, DefaultFileName) : Path.GetFullPath(_settings.ReportSigningCertPath);
    public X509Certificate2 Certificate => _cert.Value;

    private X509Certificate2 Load()
    {
        var path = CertificatePath;
        if (!File.Exists(path))
        {
            if (!string.IsNullOrWhiteSpace(_settings.ReportSigningCertPath)) throw new FileNotFoundException($"报表签名证书不存在：{path}");
            Generate(path);
        }
        var cert = X509CertificateLoader.LoadPkcs12FromFile(path, _settings.ReportSigningCertPassword, X509KeyStorageFlags.Exportable);
        if (!cert.HasPrivateKey) throw new InvalidOperationException("报表签名证书缺少私钥");
        if (cert.NotAfter < DateTime.Now) _logger.LogWarning("报表签名证书已过期（{NotAfter}），请更换", cert.NotAfter);
        return cert;
    }

    private void Generate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest($"CN=Scada Report Signing ({Environment.MachineName}), O=Scada, OU=Self-signed", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, cert.Export(X509ContentType.Pfx, _settings.ReportSigningCertPassword));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path);
        _logger.LogWarning("未配置报表签名证书，已生成自签名证书 {Path}（指纹 {Thumbprint}）。对外具法律效力的签名请配置 CA 颁发的证书；主备部署须同步 keys 目录。", path, cert.Thumbprint);
    }

    public (string Signature, string Thumbprint, string Subject) Sign(byte[] content, DateTimeOffset at)
    {
        var cert = Certificate;
        var cms = new SignedCms(new ContentInfo(content), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert) { DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), IncludeOption = X509IncludeOption.EndCertOnly };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(at.UtcDateTime));
        cms.ComputeSignature(signer, silent: true);
        return (Convert.ToBase64String(cms.Encode()), cert.Thumbprint, cert.Subject);
    }

    // 验证分离签名：内容摘要与签名一致，且签名证书指纹等于记录的指纹（若提供）。链信任单独报告（自签名证书链不受信任属正常）。
    public SignatureCheck Verify(byte[] content, string? signature, string? expectedThumbprint)
    {
        if (string.IsNullOrEmpty(signature)) return new(false, false, "未签名", null, null, null, false, false, false);
        try
        {
            var cms = new SignedCms(new ContentInfo(content), detached: true);
            cms.Decode(Convert.FromBase64String(signature));
            var info = cms.SignerInfos.Count == 1 ? cms.SignerInfos[0] : throw new CryptographicException("签名者数量不为 1");
            info.CheckSignature(verifySignatureOnly: true);
            var cert = info.Certificate ?? throw new CryptographicException("签名中缺少证书");
            long? signingTs = null;
            foreach (var attr in info.SignedAttributes)
                if (attr.Oid.Value == "1.2.840.113549.1.9.5") signingTs = new DateTimeOffset(new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime.ToUniversalTime(), TimeSpan.Zero).ToUnixTimeMilliseconds();
            var selfSigned = cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData);
            var chainTrusted = false;
            // 只验证证书链（含吊销）是否受信；失败仅让 chainTrusted 为 false，签名有效性已在上面验证通过。
            // 系统自签名证书走到这里必然不受信，属预期的正常结果，不返回错误。
            try { info.CheckSignature(verifySignatureOnly: false); chainTrusted = true; } catch (CryptographicException) { }
            var current = Enabled && TryCurrentThumbprint() is string t && string.Equals(t, cert.Thumbprint, StringComparison.OrdinalIgnoreCase);
            if (expectedThumbprint is not null && !string.Equals(expectedThumbprint, cert.Thumbprint, StringComparison.OrdinalIgnoreCase))
                return new(true, false, "签名证书与归档记录不一致", cert.Subject, cert.Thumbprint, signingTs, current, chainTrusted, selfSigned);
            return new(true, true, null, cert.Subject, cert.Thumbprint, signingTs, current, chainTrusted, selfSigned);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return new(true, false, "签名无效或内容已被修改：" + ex.Message, null, null, null, false, false, false);
        }
    }

    public string? TryCurrentThumbprint()
    {
        try { return Certificate.Thumbprint; } catch (Exception ex) { _logger.LogError(ex, "报表签名证书不可用"); return null; }
    }
}
