using Microsoft.EntityFrameworkCore;
using Scada.Core;
using Scada.Core.Entities;
using Scada.Core.Models;

namespace Scada.Data.RuntimeCleanup;

/// <summary>生产环境的 MustClean 实现。启动时注册一次，删除路径通过 <see cref="RuntimeCleanupExecutor"/> 调用。</summary>
public static class RuntimeCleanupHooks
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;

        RuntimeCleanupCatalog.Register("CloseOpenAlarmEvents:AlarmRule", (db, ids, ct) =>
            CloseOpenAlarmsAsync(db, db.AlarmEvents.Where(x => x.RuleId != null && ids.Contains(x.RuleId.Value)), "规则已删除", ct));
        RuntimeCleanupCatalog.Register("CloseOpenAlarmEvents:Tag", (db, ids, ct) =>
            CloseOpenAlarmsAsync(db, db.AlarmEvents.Where(x => x.TagId != null && ids.Contains(x.TagId.Value)), "测点已删除", ct));
        RuntimeCleanupCatalog.Register("CloseOpenAlarmEvents:Device", (db, ids, ct) =>
            CloseOpenAlarmsAsync(db, db.AlarmEvents.Where(x => x.DeviceId != null && ids.Contains(x.DeviceId.Value)), "设备已删除", ct));

        RuntimeCleanupCatalog.Register("OeeEndpoints.DeleteLine", async (db, ids, ct) =>
        {
            await db.OeeLineSamples.Where(x => ids.Contains(x.LineId)).ExecuteDeleteAsync(ct);
            await db.OeeRollups.Where(x => ids.Contains(x.LineId)).ExecuteDeleteAsync(ct);
            await db.DowntimeEvents.Where(x => ids.Contains(x.LineId)).ExecuteDeleteAsync(ct);
            await db.ProductionCountCorrections.Where(x => ids.Contains(x.LineId)).ExecuteDeleteAsync(ct);
        });

        RuntimeCleanupCatalog.Register("SpcChartDelete", async (db, ids, ct) =>
        {
            await db.SpcViolations.Where(x => ids.Contains(x.ChartId)).ExecuteDeleteAsync(ct);
            await db.SpcSamples.Where(x => ids.Contains(x.ChartId)).ExecuteDeleteAsync(ct);
        });

        RuntimeCleanupCatalog.Register("CalcJobConfiguration.Delete", async (db, ids, ct) =>
        {
            await db.CalcJobStates.Where(x => ids.Contains(x.JobId)).ExecuteDeleteAsync(ct);
            await db.CalcJobSnapshots.Where(x => ids.Contains(x.JobId)).ExecuteDeleteAsync(ct);
        });

        RuntimeCleanupCatalog.Register("ControlLockoutOnDelete:Tag", (db, ids, ct) =>
            ReleaseLockoutsAsync(db, db.ControlLockouts.Where(x => x.RemovedTs == null && x.TagId != null && ids.Contains(x.TagId.Value)), "测点已删除", ct));
        RuntimeCleanupCatalog.Register("ControlLockoutOnDelete:Device", (db, ids, ct) =>
            ReleaseLockoutsAsync(db, db.ControlLockouts.Where(x => x.RemovedTs == null && x.DeviceId != null && ids.Contains(x.DeviceId.Value)), "设备已删除", ct));

        RuntimeCleanupCatalog.Register("DeviceControlStateOnDelete", (db, ids, ct) =>
            db.DeviceControlStates.Where(x => ids.Contains(x.DeviceId)).ExecuteDeleteAsync(ct));

        RuntimeCleanupCatalog.Register("MomentaryLeaseOnDelete", (db, ids, ct) =>
            db.MomentaryLeases.Where(x => ids.Contains(x.TagId)).ExecuteDeleteAsync(ct));

        RuntimeCleanupCatalog.Register("AnnunciatorSilenceOnDelete", (db, ids, ct) =>
            db.AnnunciatorSilences.Where(x => ids.Contains(x.PanelId)).ExecuteDeleteAsync(ct));

        RuntimeCleanupCatalog.Register("GatewayDelete", async (db, ids, ct) =>
        {
            await db.GatewayStreams.Where(x => ids.Contains(x.GatewayId)).ExecuteDeleteAsync(ct);
            await db.GatewayArchiveCursors.Where(x => ids.Contains(x.GatewayId)).ExecuteDeleteAsync(ct);
        });

        RuntimeCleanupCatalog.Register("GatewayDeviceAlarms:Device", (db, ids, ct) =>
            db.GatewayDeviceHealth.Where(x => ids.Contains(x.DeviceId)).ExecuteDeleteAsync(ct));
        RuntimeCleanupCatalog.Register("GatewayDeviceAlarms:AcquisitionGateway", (db, ids, ct) =>
            db.GatewayDeviceHealth.Where(x => ids.Contains(x.GatewayId)).ExecuteDeleteAsync(ct));
    }

    private static async Task CloseOpenAlarmsAsync(RuntimeDbContext db, IQueryable<AlarmEvent> query, string reason, CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        var open = await query.Where(x => x.EndTs == null).ToListAsync(ct);
        foreach (var alarm in open)
        {
            alarm.EndTs = now;
            alarm.EndReason = reason;
            db.AlarmActivities.Add(new AlarmActivity { AlarmEventId = alarm.Id, Ts = now, Action = "recover", Note = reason });
        }
        if (open.Count > 0) await db.SaveChangesAsync(ct);
    }

    private static async Task ReleaseLockoutsAsync(RuntimeDbContext db, IQueryable<ControlLockout> query, string reason, CancellationToken ct)
    {
        var now = ScadaTime.NowMs();
        await query.ExecuteUpdateAsync(set => set
            .SetProperty(x => x.RemovedTs, now)
            .SetProperty(x => x.RemovedBy, "system")
            .SetProperty(x => x.RemoveReason, reason), ct);
    }
}