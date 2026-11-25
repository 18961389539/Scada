using Microsoft.Extensions.Logging;
using Scada.Core.Options;
using Scada.Engine.Health;

namespace Scada.Engine.Maintenance;

public static class BackupSecurity
{
    public const string UnencryptedWarningCode = "backup_unencrypted";

    public const string UnencryptedWarningMessage =
        "当前未配置 Scada:BackupPassword，本地备份为明文，内含密钥与口令哈希。请使用离线工具 tools/deployment/Set-BackupPassword.ps1 设置口令并重启；也可用发布包中的 Scada.BackupTool 校验加密包。在此之前异地拷贝会被拒绝。已有明文包不会被改写。";

    public static SystemWarning? UnencryptedWarning(bool development, ScadaSettings settings)
    {
        if (!development && string.IsNullOrWhiteSpace(settings.BackupPassword))
            return new(UnencryptedWarningCode, UnencryptedWarningMessage);
        return null;
    }

    public static void WarnIfUnencrypted(bool development, ScadaSettings settings, ILogger logger)
    {
        if (UnencryptedWarning(development, settings) is { } warning)
            logger.LogWarning("Backup encryption is not configured. Local ZIP bundles contain credentials and private keys. Set Scada:BackupPassword using the offline setup tool (tools/deployment/Set-BackupPassword.ps1) and restart; offsite uploads will be refused until new encrypted bundles are created. Existing plaintext bundles are not modified.");
    }

    public static void RequireEncryptedOffsite(string path)
    {
        if (!BackupArchive.IsEncrypted(path))
            throw new InvalidOperationException("Offsite upload refused: this backup is plaintext. Set Scada:BackupPassword, restart the server, create and verify a NEW encrypted backup. Existing local backups remain restorable; no files have been deleted.");
    }
}
