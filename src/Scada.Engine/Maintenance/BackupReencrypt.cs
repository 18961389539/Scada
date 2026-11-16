namespace Scada.Engine.Maintenance;

/// <summary>备份包口令轮换结果。源包保持不变，调用方确认新包可用后再决定是否删除旧包。</summary>
public sealed record BackupReencryptResult(string SourcePath, string TargetPath, long Bytes);

/// <summary>
/// 离线备份包口令轮换：解密到临时明文 ZIP，再用新口令重新加密，最后用新口令回读并逐字节比对。
///
/// 不做原地覆盖：口令轮换是一次高风险运维动作，旧包必须保留到新包通过校验为止。
/// 目标文件与源包同目录，因为保留策略、备份列表与异地复制都只扫描 <see cref="BackupService"/> 的根目录。
/// </summary>
public static class BackupReencrypt
{
    public static async Task<BackupReencryptResult> RunAsync(
        string sourcePath, string targetPath, string oldPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oldPassword)) throw new ArgumentException("旧口令不能为空", nameof(oldPassword));
        if (string.IsNullOrWhiteSpace(newPassword)) throw new ArgumentException("新口令不能为空", nameof(newPassword));
        if (string.Equals(oldPassword, newPassword, StringComparison.Ordinal))
            throw new ArgumentException("新口令不能与旧口令相同", nameof(newPassword));

        var source = Path.GetFullPath(sourcePath);
        var target = Path.GetFullPath(targetPath);
        if (!File.Exists(source)) throw new FileNotFoundException("备份不存在", source);
        if (!BackupArchive.IsEncrypted(source))
            throw new ArgumentException("只支持加密备份包；明文包不在轮换范围内", nameof(sourcePath));
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标文件不能与源文件相同，轮换必须保留旧包", nameof(targetPath));
        if (File.Exists(target)) throw new IOException($"目标文件已存在：{target}");
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标必须与源包在同一目录，保留策略与备份列表只扫描该目录", nameof(targetPath));
        if (!BackupService.IsBackupFileName(Path.GetFileName(target)))
            throw new ArgumentException("目标文件名必须符合 full_*.zip.enc", nameof(targetPath));

        var directory = Path.GetDirectoryName(target)!;
        var plain = Path.Combine(directory, ".reenc-" + Guid.NewGuid().ToString("N") + BackupService.PlainSuffix);
        var check = Path.Combine(directory, ".verify-" + Guid.NewGuid().ToString("N") + BackupService.PlainSuffix);
        var completed = false;
        try
        {
            await BackupArchive.DecryptAsync(source, plain, oldPassword, cancellationToken);
            await BackupArchive.EncryptAsync(plain, target, newPassword, cancellationToken);

            // 新包必须能用新口令解开，且内容与旧包一致；否则宁可失败也不留下一个"看起来像备份"的文件。
            await BackupArchive.DecryptAsync(target, check, newPassword, cancellationToken);
            await using (var expected = File.OpenRead(plain))
            await using (var actual = File.OpenRead(check))
            {
                if (expected.Length != actual.Length) throw new BackupArchiveException("重新加密后的内容长度不一致");
                var left = new byte[81920];
                var right = new byte[81920];
                for (int n; (n = await expected.ReadAsync(left, cancellationToken)) > 0;)
                {
                    var m = await actual.ReadAsync(right, cancellationToken);
                    if (m != n || !left.AsSpan(0, n).SequenceEqual(right.AsSpan(0, m)))
                        throw new BackupArchiveException("重新加密后的内容与原包不一致");
                }
            }

            completed = true;
            return new BackupReencryptResult(source, target, new FileInfo(target).Length);
        }
        finally
        {
            TryDelete(plain);
            TryDelete(check);
            if (!completed) TryDelete(target);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 临时文件清理失败不影响结果 */ }
    }
}