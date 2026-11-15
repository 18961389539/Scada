using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Scada.Core.Licensing;
using Scada.Core.Options;

namespace Scada.Server.Licensing;

/// <summary>
/// 授权状态机：试用 → 激活 / 过期；每分钟复查一次，时钟回拨进入 Tampered。
    /// 过期后网页接口由闸门拒绝。采集和历史继续。下发、声光、配方、顺控和外部写入看 <see cref="OutputsAllowed"/>。
/// </summary>
public sealed class LicenseService : BackgroundService, ILicenseEnforcement
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

    private (LicensePayload? Payload, string? Error) Check(string code)
    {
        if (_publicKey is null)
        {
            return (null, "服务端未配置授权公钥，无法校验激活码");
        }

        var parsed = LicenseToken.Parse(code, _publicKey);
        if (!parsed.Valid)
        {
            return (null, parsed.Error);
        }

        var payload = parsed.Payload!;
        if (!LicenseEvaluation.MatchesMachine(payload, _machineId))
        {
            return (null, "激活码不属于本机，请用当前机器码重新签发");
        }
        if (LicenseEvaluation.IsExpired(payload, _time.GetUtcNow().ToUnixTimeSeconds()))
        {
            return (null, "激活码已过期");
        }
        return (payload, null);
    }

    /// <summary>只校验并解析激活码，不保存：激活会整体替换现有授权，先让管理员看清新授权的内容。</summary>
    public (LicensePreview? Preview, string? Error) Preview(string code, int devices, int tags)
    {
        lock (_sync)
        {
            var (payload, error) = Check(code);
            if (payload is null) return (null, error);
            var expires = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
            var days = LicenseEvaluation.DaysRemaining(payload.ExpiresAt, _time.GetUtcNow().ToUnixTimeSeconds());
            return (new LicensePreview(payload.LicenseId, payload.Customer, expires, days, payload.MaxDevices, payload.MaxTags, payload.Features ?? [], devices, tags), null);
        }
    }

    public (bool Ok, string? Error) Activate(string code)
    {
        lock (_sync)
        {
            var (payload, error) = Check(code);
            if (payload is null)
            {
                return (false, error);
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
                || LicenseEvaluation.IsClockRollback(nowUnix, _state.LastSeenUtc, _options.ClockSkewSeconds))
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
                else if (!LicenseEvaluation.MatchesMachine(payload, _machineId))
                {
                    Current = Snapshot(LicenseStatus.Invalid, "激活码与本机不匹配", payload, trialEnd);
                }
                else if (LicenseEvaluation.IsExpired(payload, nowUnix))
                {
                    Current = Snapshot(LicenseStatus.Expired, $"授权已于 {DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt):yyyy-MM-dd} 到期", payload, trialEnd);
                }
                else
                {
                    var days = LicenseEvaluation.DaysRemaining(payload.ExpiresAt, nowUnix);
                    var status = LicenseEvaluation.IsExpiringSoon(days, _options.WarningDays) ? LicenseStatus.ExpiringSoon : LicenseStatus.Active;
                    Current = Snapshot(status, status == LicenseStatus.ExpiringSoon ? $"授权将在 {days} 天后到期" : "授权有效", payload, trialEnd);
                }
            }
            else if (now >= trialEnd)
            {
                Current = Snapshot(LicenseStatus.Expired, $"试用期已于 {trialEnd:yyyy-MM-dd} 结束，请激活后继续使用", null, trialEnd);
            }
            else
            {
                var days = LicenseEvaluation.DaysRemaining(trialEnd.ToUnixTimeSeconds(), nowUnix);
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
        var days = LicenseEvaluation.DaysRemaining((expires ?? trialEnd).ToUnixTimeSeconds(), now.ToUnixTimeSeconds());
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
            payload?.Features ?? (status == LicenseStatus.Trial ? LicenseFeatures.All : []),
            trialEnd,
            Math.Max(1, _options.TrialDays));
    }

    public bool OutputsAllowed => !Current.Blocked;
    public string BlockMessage => string.IsNullOrWhiteSpace(Current.Message) ? "授权已锁定，不能下发" : Current.Message;
    public int MaxDevices => Current.MaxDevices;
    public int MaxTags => Current.MaxTags;

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