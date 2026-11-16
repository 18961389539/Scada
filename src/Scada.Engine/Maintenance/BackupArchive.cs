using System.Security.Cryptography;
using System.Text;

namespace Scada.Engine.Maintenance;

/// <summary>备份包解密失败：口令不对，或密文被改过。两者刻意不区分——能区分就等于给了离线爆破的判据。</summary>
public sealed class BackupArchiveException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 完整备份的口令加密容器。
///
/// 为什么必须有：备份包里装着 Data Protection 的 <c>keys/</c>（能解出设备口令与通知密钥）、
/// <c>config.db</c>（含全部用户口令哈希）和 OPC PKI 私钥。这些文件打包成一个 ZIP 后，
/// 任何拿到包的人（发给厂商排查、拷进共享盘、随工单流转）都能直接读走认证密钥。
/// 清单里的 SHA256 只解决"有没有被改"，不解决"谁能看"。
///
/// 容器格式（自描述、可演进，全部大端）：
/// <code>
///   header  46B : magic "SCADABK1"(8) | version(1)=1 | kdf(1)=1 | iterations(4) | salt(16) | iv(16)
///   body       : AES-256-CBC / PKCS7 密文
///   trailer 32B : HMAC-SHA256(header || body)
/// </code>
///
/// 为什么是 CBC + HMAC 而不是 GCM：备份动辄数 GB，必须流式。GCM 要分块拼 nonce 并在尾部加结束标记，
/// 一旦截断就只能靠"解压失败"间接发现；Encrypt-then-MAC 是先验完整再解密，口令错与篡改都在写出
/// 任何明文字节之前就被拦下，实现也短得多。MAC 覆盖 header，攻击者无法把 iterations 调低再爆破。
/// </summary>
public static class BackupArchive
{
    private const string MagicText = "SCADABK1";
    private const byte FormatVersion = 1;
    private const byte KdfPbkdf2Sha256 = 1;
    // OWASP 对 PBKDF2-SHA256 的建议区间；工控机上约 0.2-0.5 秒，只在建包/还原时付一次。
    private const int DefaultIterations = 310_000;
    private const int MaxIterations = 5_000_000;
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int TagSize = 32;
    private const int HeaderSize = 8 + 1 + 1 + 4 + SaltSize + IvSize;
    private const int BlockSize = 16;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes(MagicText);

    /// <summary>按文件头判断是否为加密包；明文 ZIP 以 <c>PK</c> 开头，不会误判。</summary>
    public static bool IsEncrypted(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> head = stackalloc byte[Magic.Length];
        return stream.Read(head) == head.Length && head.SequenceEqual(Magic);
    }

    public static async Task EncryptAsync(string sourcePath, string targetPath, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("加密口令不能为空", nameof(password));

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var header = BuildHeader(salt, iv, DefaultIterations);
        var keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA256, 64);
        try
        {
            await using (var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await output.WriteAsync(header, cancellationToken);

                using var aes = Aes.Create();
                aes.Key = keys[..32];
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                await using (var crypto = new CryptoStream(output, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
                {
                    await input.CopyToAsync(crypto, 81920, cancellationToken);
                    crypto.FlushFinalBlock();
                }

                await output.FlushAsync(cancellationToken);
            }

            // 第二遍回读整包算 HMAC 再追加。比在写入路径上挂一个"顺带哈希"的包装流多读一次磁盘，
            // 但少一个自定义 Stream（Write/WriteAsync/Span 三条路径都要保证喂给哈希的数据与落盘字节完全一致，
            // 一旦漏一条就是"口令对却解不开"）。备份是分钟级操作，这一遍读几十秒换确定性，值。
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, keys[32..]);
            await using (var read = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                for (int n; (n = await read.ReadAsync(buffer, cancellationToken)) > 0;)
                    hmac.AppendData(buffer, 0, n);
            }

            var tag = hmac.GetHashAndReset();
            await using (var append = new FileStream(targetPath, FileMode.Append, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await append.WriteAsync(tag, cancellationToken);
                await append.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys);
        }
    }

    public static async Task DecryptAsync(string encryptedPath, string targetPath, string password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(password)) throw new BackupArchiveException("备份包已加密，未提供口令");

        await using var input = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (input.Length < HeaderSize + TagSize + BlockSize) throw new BackupArchiveException("备份包已损坏：文件长度不足");

        var header = new byte[HeaderSize];
        await input.ReadExactlyAsync(header, cancellationToken);
        var (salt, iv, iterations) = ParseHeader(header);

        var cipherLength = input.Length - HeaderSize - TagSize;
        if (cipherLength <= 0 || cipherLength % BlockSize != 0) throw new BackupArchiveException("备份包已损坏：密文长度非法");

        var keys = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 64);
        try
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, keys[32..]);
            hmac.AppendData(header);

            // 第一遍：只读密文算 HMAC，不写任何明文。口令错与篡改都在这里被拦下。
            var buffer = new byte[81920];
            for (long remaining = cipherLength; remaining > 0;)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                if (read == 0) throw new BackupArchiveException("备份包已损坏：密文被截断");
                hmac.AppendData(buffer, 0, read);
                remaining -= read;
            }

            var expected = new byte[TagSize];
            await input.ReadExactlyAsync(expected, cancellationToken);
            var actual = hmac.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                throw new BackupArchiveException("备份包口令错误或内容已损坏");

            // 第二遍：校验通过后才解密。
            input.Position = HeaderSize;
            using var aes = Aes.Create();
            aes.Key = keys[..32];
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            await using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            using (var crypto = new CryptoStream(output, aes.CreateDecryptor(), CryptoStreamMode.Write, leaveOpen: true))
            {
                for (long remaining = cipherLength; remaining > 0;)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
                    if (read == 0) throw new BackupArchiveException("备份包已损坏：密文被截断");
                    await crypto.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    remaining -= read;
                }

                crypto.FlushFinalBlock();
            }

            await output.FlushAsync(cancellationToken);
        }
        catch (CryptographicException ex)
        {
            throw new BackupArchiveException("备份包口令错误或内容已损坏", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keys);
        }
    }

    private static byte[] BuildHeader(byte[] salt, byte[] iv, int iterations)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        header[8] = FormatVersion;
        header[9] = KdfPbkdf2Sha256;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(10, 4), iterations);
        salt.CopyTo(header, 14);
        iv.CopyTo(header, 30);
        return header;
    }

    private static (byte[] Salt, byte[] Iv, int Iterations) ParseHeader(byte[] header)
    {
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new BackupArchiveException("不是 Scada 加密备份包");
        if (header[8] != FormatVersion) throw new BackupArchiveException($"备份包版本 {header[8]} 不受支持，请升级程序");
        if (header[9] != KdfPbkdf2Sha256) throw new BackupArchiveException($"备份包使用的密钥派生算法 {header[9]} 不受支持");
        var iterations = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(10, 4));
        if (iterations is < 1000 or > MaxIterations) throw new BackupArchiveException("备份包已损坏：迭代次数非法");
        return (header[14..30], header[30..46], iterations);
    }
}
