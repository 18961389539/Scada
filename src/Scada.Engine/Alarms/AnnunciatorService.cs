using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Control;

namespace Scada.Engine.Alarms;

public sealed record AnnunciatorPanelDto(
    int Id, string Name, int? AreaId, bool Enabled, AnnunciatorLatchMode LatchMode, int SilenceMinutes,
    int FlashOnMs, int FlashOffMs, int CriticalFlashOnMs, int CriticalFlashOffMs, bool BrowserSound,
    long Revision, long UpdatedTs, string? UpdatedBy,
    IReadOnlyList<AnnunciatorOutputDto> Outputs, IReadOnlyList<AnnunciatorLevelMapDto> Levels);

public sealed record AnnunciatorOutputDto(int Id, AnnunciatorChannelKind Kind, int TagId, double ActiveValue, double InactiveValue, bool Enabled);
public sealed record AnnunciatorLevelMapDto(int Id, AlarmLevel Level, bool Horn, bool Beacon, bool TowerRed, bool TowerYellow, bool TowerBlue, bool TowerGreen, bool Flash, string? SoundKey);

public sealed record AnnunciatorStatusDto(
    int PanelId, string Name, int? AreaId, bool Enabled, AlarmLevel? DrivingLevel, bool Horn, bool Beacon,
    bool TowerRed, bool TowerYellow, bool TowerBlue, bool TowerGreen, bool Flashing, bool Silenced,
    long? SilenceUntilTs, string? SoundKey, string? LastError);

public sealed record AnnunciatorPanelInput(
    string Name, int? AreaId, bool Enabled, AnnunciatorLatchMode LatchMode, int SilenceMinutes,
    int FlashOnMs, int FlashOffMs, int CriticalFlashOnMs, int CriticalFlashOffMs, bool BrowserSound,
    IReadOnlyList<AnnunciatorOutputInput>? Outputs, IReadOnlyList<AnnunciatorLevelMapInput>? Levels);

public sealed record AnnunciatorOutputInput(AnnunciatorChannelKind Kind, int TagId, double ActiveValue, double InactiveValue, bool Enabled);
public sealed record AnnunciatorLevelMapInput(AlarmLevel Level, bool Horn, bool Beacon, bool TowerRed, bool TowerYellow, bool TowerBlue, bool TowerGreen, bool Flash, string? SoundKey);

// 声光联动：监听报警事件 → 仲裁最高未确认等级 → 经声光写入路径驱动输出测点；支持消音、试验、锁存。
public sealed class AnnunciatorService : BackgroundService, IAlarmNotifier
{
    private readonly IDbContextFactory<ConfigDbContext> _factory;
    private readonly IServiceScopeFactory _scopes;
    private readonly AccessModelProvider _access;
    private readonly INodeRole _role;
    private readonly ILogger<AnnunciatorService> _logger;
    private readonly ConcurrentDictionary<int, AnnunciatorDesired> _latched = new();
    private readonly ConcurrentDictionary<int, AnnunciatorDesired> _last = new();
    private readonly ConcurrentDictionary<int, string?> _errors = new();
    private readonly ConcurrentDictionary<int, Dictionary<AnnunciatorChannelKind, double>> _written = new();
    private readonly object _gate = new();
    private Task? _reconcile;
    private bool _flashPhaseOn = true;
    private long _nextFlashToggle;

    public AnnunciatorService(
        IDbContextFactory<ConfigDbContext> factory,
        IServiceScopeFactory scopes,
        AccessModelProvider access,
        INodeRole role,
        ILogger<AnnunciatorService> logger)
    {
        _factory = factory;
        _scopes = scopes;
        _access = access;
        _role = role;
        _logger = logger;
    }

    public Task PublishAlarmAsync(AlarmEvent alarm, string action, CancellationToken cancellationToken = default)
    {
        RequestReconcile();
        return Task.CompletedTask;
    }

    public void RequestReconcile()
    {
        lock (_gate)
        {
            if (_reconcile is { IsCompleted: false }) return;
            _reconcile = Task.Run(() => ReconcileSafeAsync(CancellationToken.None));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RequestReconcile();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = ScadaTime.NowMs();
                if (now >= _nextFlashToggle)
                {
                    _flashPhaseOn = !_flashPhaseOn;
                    _nextFlashToggle = now + 250;
                    await ReconcileSafeAsync(stoppingToken);
                }
                else await Task.Delay(100, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "声光联动循环异常"); await Task.Delay(1000, stoppingToken); }
        }
    }

    private async Task ReconcileSafeAsync(CancellationToken ct)
    {
        try { await ReconcileAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "声光联动刷新失败"); }
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!_role.IsActive) return;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panels = await db.AnnunciatorPanels.AsNoTracking().Where(x => x.Enabled).OrderBy(x => x.Id).ToListAsync(ct);
        if (panels.Count == 0) return;
        var outputs = await db.AnnunciatorOutputs.AsNoTracking().Where(x => x.Enabled).ToListAsync(ct);
        var maps = await db.AnnunciatorLevelMaps.AsNoTracking().ToListAsync(ct);
        var silences = await db.AnnunciatorSilences.AsNoTracking().ToDictionaryAsync(x => x.PanelId, ct);
        var model = await _access.GetAsync(ct);
        var now = ScadaTime.NowMs();
        var alarms = await db.AlarmEvents.AsNoTracking()
            .Where(x => x.EndTs == null || x.AckTs == null)
            .OrderByDescending(x => x.Id).Take(5000).ToListAsync(ct);
        var samples = alarms.Select(a => ToSample(a, model)).ToList();

        // 刷新闪烁节拍：取所有驱动面板中最短的亮/灭。
        var flashMs = panels.Select(p =>
        {
            var level = samples.Where(s => AnnunciatorLogic.IsAnnunciable(s) && AnnunciatorLogic.MatchesPanel(s, p.AreaId)).Select(s => s.Level).DefaultIfEmpty(AlarmLevel.Info).Max();
            return level == AlarmLevel.Critical ? Math.Min(p.CriticalFlashOnMs, p.CriticalFlashOffMs) : Math.Min(p.FlashOnMs, p.FlashOffMs);
        }).DefaultIfEmpty(500).Min();
        flashMs = Math.Clamp(flashMs, 100, 5000);
        if (_nextFlashToggle < now) _nextFlashToggle = now + flashMs;

        using var scope = _scopes.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<ControlCommandService>();

        foreach (var panel in panels)
        {
            silences.TryGetValue(panel.Id, out var silence);
            var panelMaps = maps.Where(m => m.PanelId == panel.Id).ToList();
            if (panelMaps.Count == 0) panelMaps = AnnunciatorLogic.DefaultMaps(panel.Id).ToList();
            var computed = AnnunciatorLogic.Compute(samples, panel, panelMaps, now, silence?.UntilTs);
            _last.TryGetValue(panel.Id, out var previous);
            if (computed.DrivingLevel is not null) _latched[panel.Id] = computed;
            var clearLatch = silence is not null && silence.UntilTs > now; // 消音同时解除锁存灯（操作员明确消音）
            var desired = AnnunciatorLogic.ApplyLatch(computed, _latched.GetValueOrDefault(panel.Id), panel.LatchMode, clearLatch);
            if (clearLatch || computed.DrivingLevel is null && panel.LatchMode == AnnunciatorLatchMode.Follow)
                _latched.TryRemove(panel.Id, out _);
            _last[panel.Id] = desired;

            var panelOutputs = outputs.Where(o => o.PanelId == panel.Id).ToList();
            var written = _written.GetOrAdd(panel.Id, _ => new());
            foreach (var output in panelOutputs)
            {
                var phase = AnnunciatorLogic.ChannelValue(desired, output.Kind, _flashPhaseOn);
                var value = phase > 0 ? output.ActiveValue : output.InactiveValue;
                if (written.TryGetValue(output.Kind, out var prev) && Math.Abs(prev - value) < 1e-9) continue;
                var result = await commands.SubmitAnnunciatorAsync(output.TagId, value, $"声光面板 {panel.Name}/{output.Kind}", ct);
                if (result.Success) { written[output.Kind] = value; _errors[panel.Id] = null; }
                else
                {
                    _errors[panel.Id] = result.Message;
                    _logger.LogWarning("声光面板 {Panel} 通道 {Kind} 写入失败：{Message}", panel.Name, output.Kind, result.Message);
                }
            }
        }
    }

    private static AnnunciatorAlarmSample ToSample(AlarmEvent a, AccessModel model)
    {
        int? area = a.TagId is int tagId ? model.AreasOfTag(tagId).FirstOrDefault()
            : a.DeviceId is int deviceId && model.DeviceAreas.TryGetValue(deviceId, out var da) ? da
            : null;
        return new(a.Level, area, a.EndTs is null, a.AckTs is null, a.Shelved, a.Suppressed, a.Chattering);
    }

    public async Task<IReadOnlyList<AnnunciatorPanelDto>> ListAsync(Func<int?, bool> areaVisible, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panels = await db.AnnunciatorPanels.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var outputs = await db.AnnunciatorOutputs.AsNoTracking().ToListAsync(ct);
        var maps = await db.AnnunciatorLevelMaps.AsNoTracking().ToListAsync(ct);
        return panels.Where(p => areaVisible(p.AreaId)).Select(p => ToDto(p,
            outputs.Where(o => o.PanelId == p.Id).ToList(),
            maps.Where(m => m.PanelId == p.Id).ToList())).ToList();
    }

    public async Task<AnnunciatorPanelDto?> GetAsync(int id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panel = await db.AnnunciatorPanels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (panel is null) return null;
        var outputs = await db.AnnunciatorOutputs.AsNoTracking().Where(x => x.PanelId == id).ToListAsync(ct);
        var maps = await db.AnnunciatorLevelMaps.AsNoTracking().Where(x => x.PanelId == id).ToListAsync(ct);
        return ToDto(panel, outputs, maps);
    }

    public async Task<(AnnunciatorPanelDto? Dto, string? Error)> UpsertAsync(int? id, AnnunciatorPanelInput input, string user, CancellationToken ct)
    {
        var err = Validate(input);
        if (err is not null) return (null, err);
        await using var db = await _factory.CreateDbContextAsync(ct);
        foreach (var o in input.Outputs ?? [])
        {
            var tag = await db.Tags.AsNoTracking().SingleOrDefaultAsync(x => x.Id == o.TagId, ct);
            if (tag is null) return (null, $"输出测点 #{o.TagId} 不存在");
            if (tag.Kind != TagKind.Physical || !tag.Writable) return (null, $"输出测点 #{o.TagId} 必须是可写物理测点");
        }
        if (await db.AnnunciatorPanels.AnyAsync(x => x.Name == input.Name.Trim() && x.Id != (id ?? 0), ct))
            return (null, "面板名称已存在");

        AnnunciatorPanel panel;
        if (id is int existingId)
        {
            panel = await db.AnnunciatorPanels.SingleOrDefaultAsync(x => x.Id == existingId, ct) ?? throw new InvalidOperationException("not found");
            panel.Revision++;
            db.AnnunciatorOutputs.RemoveRange(db.AnnunciatorOutputs.Where(x => x.PanelId == panel.Id));
            db.AnnunciatorLevelMaps.RemoveRange(db.AnnunciatorLevelMaps.Where(x => x.PanelId == panel.Id));
        }
        else
        {
            panel = new AnnunciatorPanel();
            db.AnnunciatorPanels.Add(panel);
        }
        Apply(panel, input, user);
        await db.SaveChangesAsync(ct);

        var outputs = (input.Outputs ?? []).Select(o => new AnnunciatorOutput
        {
            PanelId = panel.Id, Kind = o.Kind, TagId = o.TagId, ActiveValue = o.ActiveValue, InactiveValue = o.InactiveValue, Enabled = o.Enabled
        }).ToList();
        var levels = (input.Levels is { Count: > 0 } list ? list : DefaultLevelInputs())
            .Select(l => new AnnunciatorLevelMap
            {
                PanelId = panel.Id, Level = l.Level, Horn = l.Horn, Beacon = l.Beacon,
                TowerRed = l.TowerRed, TowerYellow = l.TowerYellow, TowerBlue = l.TowerBlue, TowerGreen = l.TowerGreen,
                Flash = l.Flash, SoundKey = string.IsNullOrWhiteSpace(l.SoundKey) ? null : l.SoundKey.Trim()
            }).ToList();
        // 每种通道至多一个输出
        if (outputs.GroupBy(x => x.Kind).Any(g => g.Count() > 1)) return (null, "同一通道只能绑定一个输出测点");
        if (levels.GroupBy(x => x.Level).Any(g => g.Count() > 1)) return (null, "同一等级只能有一条映射");
        db.AnnunciatorOutputs.AddRange(outputs);
        db.AnnunciatorLevelMaps.AddRange(levels);
        await db.SaveChangesAsync(ct);
        _written.TryRemove(panel.Id, out _);
        RequestReconcile();
        return (await GetAsync(panel.Id, ct), null);
    }

    public async Task<string?> DeleteAsync(int id, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panel = await db.AnnunciatorPanels.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (panel is null) return "not found";
        db.AnnunciatorPanels.Remove(panel);
        var silence = await db.AnnunciatorSilences.SingleOrDefaultAsync(x => x.PanelId == id, ct);
        if (silence is not null) db.AnnunciatorSilences.Remove(silence);
        await db.SaveChangesAsync(ct);
        _latched.TryRemove(id, out _);
        _last.TryRemove(id, out _);
        _written.TryRemove(id, out _);
        RequestReconcile();
        return null;
    }

    public async Task<(AnnunciatorStatusDto? Status, string? Error)> SilenceAsync(int panelId, int? minutes, string? reason, string user, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panel = await db.AnnunciatorPanels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == panelId, ct);
        if (panel is null) return (null, "not found");
        var mins = Math.Clamp(minutes ?? panel.SilenceMinutes, 1, 120);
        var now = ScadaTime.NowMs();
        var row = await db.AnnunciatorSilences.SingleOrDefaultAsync(x => x.PanelId == panelId, ct);
        if (row is null) { row = new AnnunciatorSilence { PanelId = panelId }; db.AnnunciatorSilences.Add(row); }
        row.StartedTs = now;
        row.UntilTs = now + mins * 60_000L;
        row.ByUser = user;
        row.Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        db.OperationLogs.Add(new OperationLog
        {
            UserName = user, Ts = now, Action = $"声光消音 {panel.Name}", Result = $"消音 {mins} 分钟",
            TargetType = "AnnunciatorPanel", TargetId = panelId.ToString(), Details = row.Reason
        });
        await db.SaveChangesAsync(ct);
        _latched.TryRemove(panelId, out _);
        RequestReconcile();
        return (await StatusOfAsync(panelId, ct), null);
    }

    public async Task<(AnnunciatorStatusDto? Status, string? Error)> ClearSilenceAsync(int panelId, string user, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panel = await db.AnnunciatorPanels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == panelId, ct);
        if (panel is null) return (null, "not found");
        var row = await db.AnnunciatorSilences.SingleOrDefaultAsync(x => x.PanelId == panelId, ct);
        if (row is not null) db.AnnunciatorSilences.Remove(row);
        db.OperationLogs.Add(new OperationLog
        {
            UserName = user, Ts = ScadaTime.NowMs(), Action = $"声光取消消音 {panel.Name}", Result = "成功",
            TargetType = "AnnunciatorPanel", TargetId = panelId.ToString()
        });
        await db.SaveChangesAsync(ct);
        RequestReconcile();
        return (await StatusOfAsync(panelId, ct), null);
    }

    public async Task<(AnnunciatorStatusDto? Status, string? Error)> TestAsync(int panelId, string user, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panel = await db.AnnunciatorPanels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == panelId, ct);
        if (panel is null) return (null, "not found");
        var outputs = await db.AnnunciatorOutputs.AsNoTracking().Where(x => x.PanelId == panelId && x.Enabled).ToListAsync(ct);
        using var scope = _scopes.CreateScope();
        var commands = scope.ServiceProvider.GetRequiredService<ControlCommandService>();
        foreach (var output in outputs)
            await commands.SubmitAnnunciatorAsync(output.TagId, output.ActiveValue, $"声光试验 {panel.Name}/{output.Kind}", ct);
        db.OperationLogs.Add(new OperationLog
        {
            UserName = user, Ts = ScadaTime.NowMs(), Action = $"声光试验 {panel.Name}", Result = "已触发全部输出",
            TargetType = "AnnunciatorPanel", TargetId = panelId.ToString()
        });
        await db.SaveChangesAsync(ct);
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            _written.TryRemove(panelId, out _);
            RequestReconcile();
        });
        return (await StatusOfAsync(panelId, ct), null);
    }

    public async Task<IReadOnlyList<AnnunciatorStatusDto>> StatusAsync(Func<int?, bool> areaVisible, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panels = await db.AnnunciatorPanels.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var list = new List<AnnunciatorStatusDto>();
        foreach (var p in panels.Where(x => areaVisible(x.AreaId)))
            if (await StatusOfAsync(p.Id, ct) is { } s) list.Add(s);
        return list;
    }

    private async Task<AnnunciatorStatusDto?> StatusOfAsync(int panelId, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var panel = await db.AnnunciatorPanels.AsNoTracking().SingleOrDefaultAsync(x => x.Id == panelId, ct);
        if (panel is null) return null;
        var silence = await db.AnnunciatorSilences.AsNoTracking().SingleOrDefaultAsync(x => x.PanelId == panelId, ct);
        _last.TryGetValue(panelId, out var desired);
        desired ??= AnnunciatorDesired.Idle(silence is not null && silence.UntilTs > ScadaTime.NowMs());
        return new AnnunciatorStatusDto(
            panel.Id, panel.Name, panel.AreaId, panel.Enabled, desired.DrivingLevel, desired.Horn, desired.Beacon,
            desired.TowerRed, desired.TowerYellow, desired.TowerBlue, desired.TowerGreen, desired.Flashing, desired.Silenced,
            silence is not null && silence.UntilTs > ScadaTime.NowMs() ? silence.UntilTs : null,
            desired.SoundKey, _errors.GetValueOrDefault(panelId));
    }

    private static AnnunciatorPanelDto ToDto(AnnunciatorPanel p, List<AnnunciatorOutput> outputs, List<AnnunciatorLevelMap> maps) =>
        new(p.Id, p.Name, p.AreaId, p.Enabled, p.LatchMode, p.SilenceMinutes, p.FlashOnMs, p.FlashOffMs, p.CriticalFlashOnMs, p.CriticalFlashOffMs,
            p.BrowserSound, p.Revision, p.UpdatedTs, p.UpdatedBy,
            outputs.OrderBy(x => x.Kind).Select(o => new AnnunciatorOutputDto(o.Id, o.Kind, o.TagId, o.ActiveValue, o.InactiveValue, o.Enabled)).ToList(),
            maps.OrderByDescending(x => x.Level).Select(m => new AnnunciatorLevelMapDto(m.Id, m.Level, m.Horn, m.Beacon, m.TowerRed, m.TowerYellow, m.TowerBlue, m.TowerGreen, m.Flash, m.SoundKey)).ToList());

    private static void Apply(AnnunciatorPanel panel, AnnunciatorPanelInput input, string user)
    {
        panel.Name = input.Name.Trim();
        panel.AreaId = input.AreaId;
        panel.Enabled = input.Enabled;
        panel.LatchMode = input.LatchMode;
        panel.SilenceMinutes = Math.Clamp(input.SilenceMinutes, 1, 120);
        panel.FlashOnMs = Math.Clamp(input.FlashOnMs, 100, 5000);
        panel.FlashOffMs = Math.Clamp(input.FlashOffMs, 100, 5000);
        panel.CriticalFlashOnMs = Math.Clamp(input.CriticalFlashOnMs, 50, 5000);
        panel.CriticalFlashOffMs = Math.Clamp(input.CriticalFlashOffMs, 50, 5000);
        panel.BrowserSound = input.BrowserSound;
        panel.UpdatedTs = ScadaTime.NowMs();
        panel.UpdatedBy = user;
        if (panel.Revision <= 0) panel.Revision = 1;
    }

    private static string? Validate(AnnunciatorPanelInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 100) return "名称必填且不超过 100 字";
        if (input.SilenceMinutes is < 1 or > 120) return "消音时长须在 1～120 分钟";
        return null;
    }

    private static IReadOnlyList<AnnunciatorLevelMapInput> DefaultLevelInputs() =>
    [
        new(AlarmLevel.Critical, true, true, true, false, false, false, true, "critical"),
        new(AlarmLevel.Warning, true, true, false, true, false, false, true, "warning"),
        new(AlarmLevel.Info, false, true, false, false, true, false, false, "info")
    ];
}
