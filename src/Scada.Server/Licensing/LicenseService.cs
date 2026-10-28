using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Scada.Core.Licensing;
using Scada.Core.Options;

namespace Scada.Server.Licensing;

/// <summary>
/// 授权状态机：试用 → 激活 / 过期；每分钟复查一次，时钟回拨进入 Tampered。
/// 状态只影响“是否允许使用功能”，不参与采集与历史写入的调度。
/// </summary>
public sealed class LicenseService : BackgroundService
{
    private const string EmbeddedPublicKeyResource = "Scada.Server.Licensing.license-public.pem";

    private readonly object _sync = new();
    private readonly LicenseStateStore _store;
    private readonly LicenseOptions _options;
    private readonly ILogger<LicenseService> _logger;
    private readonly TimeProvider _time;
    private readonly ECDsa? _publicKey;
    private readonly string _machineId;
    private LicenseStateFile _state;
    private DateTimeOffset _lastPersist = DateTimeOffset.MinValue;

    public LicenseService(
        IOptions<ScadaSettings> settings,
        IOptions<LicenseOptions> options,
        ILogger<LicenseService> logger,
        TimeProvider time)
    {
        _options = options.Value;
        _logger = logger;
        _time = time;
        _store = new LicenseStateStore(settings.Value.ConfigDbPath);
        _machineId = MachineFingerprint.Compute();
        _publicKey = LoadPublicKey(_options.PublicKey);
        _state = _store.Load();
        if (_state.TrialStartedAt <= 0)
        {
            _state.TrialStartedAt = _time.GetUtcNow().ToUnixTimeSeconds();
            _store.Save(_state);
        }
        Refresh(forcePersist: true);
    }

    /// <summary>当前授权快照；线程安全（中间件与端点共读）。</summary>
    public LicenseSnapshot Current { get; private set; } = null!;

    public string StatePath => _store.FilePath;

    public (bool Ok, string? Error) Activate(string code)
    {
        lock (_sync)
        {
            if (_publicKey is null)
            {
                return (false, "服务端未配置授权公钥，无法校验激活码");
            }

            var parsed = LicenseToken.Parse(code, _publicKey);
            if (!parsed.Valid)
            {
                return (false, parsed.Error);
            }

            var payload = parsed.Payload!;
            if (!string.IsNullOrWhiteSpace(payload.MachineId)
                && !string.Equals(MachineFingerprint.Normalize(payload.MachineId), MachineFingerprint.Normalize(_machineId), StringComparison.OrdinalIgnoreCase))
            {
                return (false, "激活码不属于本机，请用当前机器码重新签发");
            }
            if (payload.ExpiresAt <= _time.GetUtcNow().ToUnixTimeSeconds())
            {
                return (false, "激活码已过期");
            }

            _state.ActivationCode = code.Trim();
            // 重新激活是特权操作：允许把误判的时钟回拨状态恢复，但也意味着改时间不能单独解锁。
            _state.ClockTampered = false;
            _state.LastSeenUtc = _time.GetUtcNow().ToUnixTimeSeconds();
            _store.Save(_state);
            Refresh(forcePersist: true);
            _logger.LogInformation("授权已激活：{LicenseId} / {Customer}，到期 {ExpiresAt:yyyy-MM-dd}", payload.LicenseId, payload.Customer,
                DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt));
            return (true, null);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            Refresh();
        }
    }

    private void Refresh(bool forcePersist = false)
    {
        lock (_sync)
        {
            var previous = Current;
            var now = _time.GetUtcNow();
            var nowUnix = now.ToUnixTimeSeconds();
            var trialEnd = DateTimeOffset.FromUnixTimeSeconds(_state.TrialStartedAt).AddDays(Math.Max(1, _options.TrialDays));

            if (_state.ClockTampered
                || (_state.LastSeenUtc > 0 && nowUnix < _state.LastSeenUtc - Math.Max(0, _options.ClockSkewSeconds)))
            {
                _state.ClockTampered = true;
                Current = Snapshot(LicenseStatus.Tampered, "检测到系统时间被回拨，授权已锁定；请校准时间后重新激活", null, trialEnd);
            }
            else if (!_options.Enabled)
            {
                Current = Snapshot(LicenseStatus.Active, "授权校验已关闭（Scada:License:Enabled=false）", null, trialEnd);
            }
            else if (_publicKey is null)
            {
                Current = Snapshot(LicenseStatus.Invalid, "服务端未配置授权公钥，全部功能不可用", null, trialEnd);
            }
            else if (!string.IsNullOrWhiteSpace(_state.ActivationCode))
            {
                var parsed = LicenseToken.Parse(_state.ActivationCode, _publicKey);
                var payload = parsed.Payload;
                if (!parsed.Valid || payload is null)
                {
                    Current = Snapshot(LicenseStatus.Invalid, parsed.Error ?? "激活码无效", null, trialEnd);
                }
                else if (!string.IsNullOrWhiteSpace(payload.MachineId)
                         && !string.Equals(MachineFingerprint.Normalize(payload.MachineId), MachineFingerprint.Normalize(_machineId), StringComparison.OrdinalIgnoreCase))
                {
                    Current = Snapshot(LicenseStatus.Invalid, "激活码与本机不匹配", payload, trialEnd);
                }
                else if (payload.ExpiresAt <= nowUnix)
                {
                    Current = Snapshot(LicenseStatus.Expired, $"授权已于 {DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt):yyyy-MM-dd} 到期", payload, trialEnd);
                }
                else
                {
                    var expires = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
                    var days = Math.Max(0, (int)Math.Ceiling((expires - now).TotalDays));
                    var status = days <= Math.Max(1, _options.WarningDays) ? LicenseStatus.ExpiringSoon : LicenseStatus.Active;
                    Current = Snapshot(status, status == LicenseStatus.ExpiringSoon ? $"授权将在 {days} 天后到期" : "授权有效", payload, trialEnd);
                }
            }
            else if (now >= trialEnd)
            {
                Current = Snapshot(LicenseStatus.Expired, $"试用期已于 {trialEnd:yyyy-MM-dd} 结束，请激活后继续使用", null, trialEnd);
            }
            else
            {
                var days = Math.Max(0, (int)Math.Ceiling((trialEnd - now).TotalDays));
                Current = Snapshot(LicenseStatus.Trial, $"试用中，剩余 {days} 天", null, trialEnd);
            }

            if (nowUnix > _state.LastSeenUtc)
            {
                _state.LastSeenUtc = nowUnix;
            }
            if (forcePersist || now - _lastPersist > TimeSpan.FromMinutes(5))
            {
                _store.Save(_state);
                _lastPersist = now;
            }

            if (previous?.Status != Current.Status || previous?.DaysRemaining != Current.DaysRemaining)
            {
                Log(Current);
            }
        }
    }

    private LicenseSnapshot Snapshot(LicenseStatus status, string message, LicensePayload? payload, DateTimeOffset trialEnd)
    {
        var now = _time.GetUtcNow();
        var expires = payload is null ? (DateTimeOffset?)null : DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
        // 试用期也要给出剩余天数：前端按它决定何时弹提醒。
        var days = Math.Max(0, (int)Math.Ceiling(((expires ?? trialEnd) - now).TotalDays));
        return new LicenseSnapshot(
            status,
            _machineId,
            message,
            payload?.LicenseId,
            payload?.Customer,
            expires,
            days,
            payload?.MaxDevices ?? 0,
            payload?.MaxTags ?? 0,
            payload?.Features ?? [],
            trialEnd,
            Math.Max(1, _options.TrialDays));
    }

    private void Log(LicenseSnapshot snapshot)
    {
        if (snapshot.Status is LicenseStatus.Expired or LicenseStatus.Invalid or LicenseStatus.Tampered)
        {
            _logger.LogWarning("授权状态 {Status}：{Message}（机器码 {MachineId}）", snapshot.Status, snapshot.Message, snapshot.MachineId);
        }
        else if (snapshot.Status == LicenseStatus.ExpiringSoon)
        {
            _logger.LogWarning("授权状态 {Status}：{Message}", snapshot.Status, snapshot.Message);
        }
        else
        {
            _logger.LogInformation("授权状态 {Status}：{Message}", snapshot.Status, snapshot.Message);
        }
    }

    private static ECDsa? LoadPublicKey(string? configured)
    {
        var pem = configured;
        if (string.IsNullOrWhiteSpace(pem))
        {
            using var stream = typeof(LicenseService).Assembly.GetManifestResourceStream(EmbeddedPublicKeyResource);
            if (stream is not null)
            {
                using var reader = new StreamReader(stream);
                pem = reader.ReadToEnd();
            }
        }
        if (string.IsNullOrWhiteSpace(pem)) return null;

        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }
}