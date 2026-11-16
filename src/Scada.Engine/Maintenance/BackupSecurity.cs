using Microsoft.Extensions.Logging;
using Scada.Core.Options;

namespace Scada.Engine.Maintenance;

public static class BackupSecurity
{
    public static void WarnIfUnencrypted(bool development, ScadaSettings settings, ILogger logger)
    {
        if (!development && string.IsNullOrWhiteSpace(settings.BackupPassword))
            logger.LogWarning("Backup encryption is not configured. Local ZIP bundles contain credentials and private keys. Set Scada:BackupPassword using the offline setup tool and restart; offsite uploads will be refused until new encrypted bundles are created. Existing plaintext bundles are not modified.");
    }

    public static void RequireEncryptedOffsite(string path)
    {
        if (!BackupArchive.IsEncrypted(path))
            throw new InvalidOperationException("Offsite upload refused: this backup is plaintext. Set Scada:BackupPassword, restart the server, create and verify a NEW encrypted backup. Existing local backups remain restorable; no files have been deleted.");
    }
}
