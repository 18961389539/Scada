using System.IO.Compression;
using System.Security.Cryptography;

namespace Scada.Server.Ha;

/// <summary>
/// 通过已认证的节点共享密钥通道同步 keys/（含报表签名证书与 HA 密钥环证书）和 OPC PKI。
/// 该通道只做认证，HTTP 明文下密钥环会以明文传输，生产环境应使用 HTTPS，并限制落盘 ACL。
/// </summary>
public static class HaSecretSync
{
    public const string ZipName = "outgoing-secrets.zip";

    public static (string Path, string Sha, long Bytes)? Snapshot(string stateDir, string keysDir, string? opcPkiRoot)
    {
        Directory.CreateDirectory(stateDir);
        var staging = Path.Combine(stateDir, ".secrets-staging-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(stateDir, ZipName);
        try
        {
            Directory.CreateDirectory(staging);
            var copied = 0;
            copied += CopyTree(keysDir, Path.Combine(staging, "keys"));
            if (!string.IsNullOrWhiteSpace(opcPkiRoot))
                copied += CopyTree(opcPkiRoot, Path.Combine(staging, "opc-pki"));
            if (copied == 0) return null;
            var temp = target + ".tmp";
            File.Delete(temp);
            ZipFile.CreateFromDirectory(staging, temp, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(temp, target, true);
            var sha = HaReplication.Hash(target);
            return (target, sha, new FileInfo(target).Length);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    public static void Apply(string zipPath, string keysDir, string? opcPkiRoot)
    {
        var extract = zipPath + ".extract-" + Guid.NewGuid().ToString("N");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, extract, overwriteFiles: true);
            var keysSource = Path.Combine(extract, "keys");
            if (Directory.Exists(keysSource))
            {
                Directory.CreateDirectory(keysDir);
                CopyTree(keysSource, keysDir);
                HaDataProtection.RestrictDirectory(keysDir);
            }
            if (!string.IsNullOrWhiteSpace(opcPkiRoot))
            {
                var pkiSource = Path.Combine(extract, "opc-pki");
                if (Directory.Exists(pkiSource))
                {
                    Directory.CreateDirectory(opcPkiRoot);
                    CopyTree(pkiSource, opcPkiRoot);
                    HaDataProtection.RestrictDirectory(opcPkiRoot);
                }
            }
        }
        finally
        {
            try { if (Directory.Exists(extract)) Directory.Delete(extract, true); } catch { }
        }
    }

    private static int CopyTree(string source, string target)
    {
        if (!Directory.Exists(source)) return 0;
        Directory.CreateDirectory(target);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
            count++;
        }
        return count;
    }
}
