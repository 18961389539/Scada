using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Scada.Server.Ha;

/// <summary>
/// 主备启用时共用同一 Data Protection 密钥环：Windows 双机不再绑定本机 DPAPI，
/// 改为证书保护（口令来自已有的 Ha:SharedKey）并收紧目录 ACL。
/// 单机 Windows 仍使用 DPAPI。不引入外部 KMS，也不提供在线改密接口。
/// </summary>
public static class HaDataProtection
{
    public const string CertificateFileName = "ha-dataprotection.pfx";
    public const string SentinelFileName = "ha-decrypt-sentinel";
    public const string SentinelPurpose = "Scada.Ha.KeyRingSentinel.v1";
    public const string CertificateSubject = "CN=Scada HA Data Protection";

    public static bool UseMachineDpapi(bool haEnabled) => !haEnabled && OperatingSystem.IsWindows();

    public static void Configure(IDataProtectionBuilder builder, DirectoryInfo keysDir, bool haEnabled, string? sharedKey)
    {
        Directory.CreateDirectory(keysDir.FullName);
        builder.SetApplicationName("Scada").PersistKeysToFileSystem(keysDir);
        if (haEnabled)
        {
            RestrictDirectory(keysDir.FullName);
            var cert = GetOrCreateCertificate(keysDir.FullName, sharedKey);
            TryMigrateDpapiKeys(keysDir.FullName, cert);
            if (cert is not null)
            {
                builder.ProtectKeysWithCertificate(cert);
                builder.UnprotectKeysWithAnyCertificate(cert);
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            builder.ProtectKeysWithDpapi();
        }
        else
        {
            RestrictDirectory(keysDir.FullName);
        }
    }

    public static X509Certificate2? GetOrCreateCertificate(string keysDir, string? sharedKey)
    {
        if (string.IsNullOrEmpty(sharedKey) || sharedKey.Length < 32) return null;
        Directory.CreateDirectory(keysDir);
        var path = Path.Combine(keysDir, CertificateFileName);
        if (File.Exists(path))
        {
            try { return X509CertificateLoader.LoadPkcs12FromFile(path, sharedKey, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable); }
            catch (CryptographicException) { return null; }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(CertificateSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, sharedKey));
        RestrictDirectory(keysDir);
        return cert;
    }

    /// <summary>
    /// 把仍由本机 DPAPI 保护的密钥改写成对端也能打开的形式。只应在原主机上调用一次。
    /// </summary>
    public static int TryMigrateDpapiKeys(string keysDir, X509Certificate2? cert)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(keysDir)) return 0;
        var migrated = 0;
        var decryptor = new DpapiXmlDecryptor();
        foreach (var file in Directory.EnumerateFiles(keysDir, "key-*.xml"))
        {
            try
            {
                var doc = XElement.Load(file);
                var encrypted = doc.Descendants().FirstOrDefault(e => Attr(e, "decrypterType") is { } type
                    && type.Contains("Dpapi", StringComparison.OrdinalIgnoreCase)
                    && e.Name.LocalName == "encryptedKey");
                if (encrypted is null) continue;
                var payload = encrypted.Elements().FirstOrDefault(e => e.Name.LocalName == "encryptedKey") ?? encrypted;
                var plain = decryptor.Decrypt(payload);
                encrypted.ReplaceWith(plain);
                var temp = file + ".tmp";
                doc.Save(temp);
                File.Move(temp, file, true);
                migrated++;
            }
            catch (Exception)
            {
                // 解密失败（例如已经在另一台机器上）留给健康自检报告，不阻断启动。
            }
        }
        return migrated;
    }

    public static void RestrictDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        if (OperatingSystem.IsWindows()) RestrictWindows(directory);
        else RestrictUnix(directory);
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string directory)
    {
        var info = new DirectoryInfo(directory);
        var acl = info.GetAccessControl();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            acl.RemoveAccessRule(rule);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        void Allow(SecurityIdentifier id) =>
            acl.AddAccessRule(new FileSystemAccessRule(id, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        var current = WindowsIdentity.GetCurrent().User;
        if (current is not null) Allow(current);
        Allow(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
        Allow(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        info.SetAccessControl(acl);
        foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var fileAcl = file.GetAccessControl();
            fileAcl.SetAccessRuleProtection(true, false);
            foreach (FileSystemAccessRule rule in fileAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                fileAcl.RemoveAccessRule(rule);
            if (current is not null)
                fileAcl.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, AccessControlType.Allow));
            fileAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            fileAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(fileAcl);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void RestrictUnix(string directory)
    {
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        foreach (var sub in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string? Attr(XElement element, string name) =>
        element.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
}
