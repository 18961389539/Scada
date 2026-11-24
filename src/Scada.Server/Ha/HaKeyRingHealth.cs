using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Data;
using Scada.Engine.Health;
using Scada.Engine.Maintenance;
using Scada.Server.Services;

namespace Scada.Server.Ha;

/// <summary>
/// 密钥环解密自检：优先解开哨兵，否则尝试已有设备口令或通知密钥密文。失败写入健康告警。
/// </summary>
public sealed class HaKeyRingHealth(
    IDataProtectionProvider protection,
    IOptions<ScadaSettings> settings,
    IOptions<HaOptions> ha,
    IDbContextFactory<ConfigDbContext> factory,
    IDeviceCredentials credentials,
    ISecretProtector secrets,
    ILogger<HaKeyRingHealth> logger) : IHealthContributor
{
    private volatile string? _error;
    private int _checked;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var keysDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settings.Value.ConfigDbPath))!, "keys");
            Directory.CreateDirectory(keysDir);
            var protector = protection.CreateProtector(HaDataProtection.SentinelPurpose);
            var sentinel = Path.Combine(keysDir, HaDataProtection.SentinelFileName);
            if (File.Exists(sentinel))
            {
                protector.Unprotect(await File.ReadAllTextAsync(sentinel, ct));
            }
            else if (!await TryExistingCiphertextAsync(ct))
            {
                await File.WriteAllTextAsync(sentinel, protector.Protect("scada-ha-sentinel"), ct);
                if (ha.Value.Enabled) HaDataProtection.RestrictDirectory(keysDir);
            }
            _error = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _error = "Data Protection 密钥环无法解密设备口令或通知密钥：" + ex.Message
                     + (ha.Value.Enabled ? "。主备节点必须共用同一密钥环；请确认已从主机同步 keys/ 且未再使用本机 DPAPI。" : "");
            logger.LogError(ex, "密钥环解密自检失败");
        }
        finally { Interlocked.Exchange(ref _checked, 1); }
    }

    public IEnumerable<SystemWarning> HealthWarnings()
    {
        if (_error is { } error) yield return new("ha_keyring_unprotect", error);
    }

    private async Task<bool> TryExistingCiphertextAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking()
            .Where(x => x.OpcPasswordProtected != null && x.OpcPasswordProtected != "")
            .Select(x => x.OpcPasswordProtected)
            .FirstOrDefaultAsync(ct);
        if (device is not null)
        {
            credentials.Unprotect(device);
            return true;
        }

        var channel = await db.NotificationChannels.AsNoTracking()
            .Where(x => x.SecretProtected != null && x.SecretProtected != "")
            .Select(x => x.SecretProtected)
            .FirstOrDefaultAsync(ct);
        if (channel is not null)
        {
            secrets.Unprotect(channel);
            return true;
        }

        return false;
    }
}

public sealed class HaKeyRingHealthHost(HaKeyRingHealth health) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await health.CheckAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await health.CheckAsync(stoppingToken);
    }
}
