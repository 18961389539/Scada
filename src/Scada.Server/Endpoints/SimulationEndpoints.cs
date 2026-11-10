using Microsoft.EntityFrameworkCore;
using Scada.Collector.Simulation;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Core.Simulation;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record SimulationForceRequest(int TagId, double Value);
public sealed record SimulationFaultRequest(string? Kind, int DurationSec = 300);
public sealed record SimulationTagView(int TagId, string Name, string DataType, double Value, bool Forced, string Quality, string? Error);
public sealed record SimulationView(
    int DeviceId, string DeviceName, bool Enabled, string Waveform, int PeriodSec,
    double? Min, double? Max, double NoisePercent, string Fault, long FaultUntilTs, IReadOnlyList<SimulationTagView> Items);

public static class SimulationEndpoints
{
    public static void MapSimulationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/simulation").AddEndpointFilter<AuditFailuresFilter>().RequireAuthorization(AccessPolicies.View);

        group.MapGet("/{deviceId:int}", async (
            int deviceId,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            SimulationRuntime runtime,
            CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null || !access.Has(Permission.View, device.AreaId)) return Results.NotFound();
            var view = await LoadAsync(db, device, runtime, ct);
            return view is null ? EndpointHelpers.BadRequest("该设备未启用仿真") : Results.Ok(view);
        }).WithSummary("Read the simulation snapshot of a device");

        group.MapPost("/{deviceId:int}/force", async (
            int deviceId,
            SimulationForceRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            SimulationRuntime runtime,
            CancellationToken ct) =>
        {
            if (!double.IsFinite(request.Value)) return EndpointHelpers.BadRequest("仿真值必须是有限数值");
            if (await EndpointHelpers.RequireTagsAsync(http, [request.TagId], Permission.Control, $"仿真强制：设备 #{deviceId}") is { } denied) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(ct);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null || !device.SimulationEnabled) return EndpointHelpers.BadRequest("该设备未启用仿真");
            var tag = await db.Tags.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.TagId && x.DeviceId == deviceId, ct);
            if (tag is null) return Results.NotFound();
            runtime.Force(deviceId, tag.Id, request.Value);
            runtimeDb.OperationLogs.Add(Audit.Change(http, $"仿真强制：{device.Name}/{tag.Name}", "Tag", tag.Id, null, new { request.Value }));
            await db.SaveChangesAsync(ct);
            await runtimeDb.SaveChangesAsync(ct);
            return Results.Ok(new { tagId = tag.Id, value = request.Value, forced = true });
        }).WithSummary("Force a raw value on a simulated tag");

        group.MapPost("/{deviceId:int}/release", async (
            int deviceId,
            SimulationReleaseRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            SimulationRuntime runtime,
            CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(ct);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null || !device.SimulationEnabled) return EndpointHelpers.BadRequest("该设备未启用仿真");
            if (request.TagId is int tagId)
            {
                if (await EndpointHelpers.RequireTagsAsync(http, [tagId], Permission.Control, $"释放仿真强制：设备 #{deviceId}") is { } denied) return denied;
                var released = runtime.Release(deviceId, tagId);
                runtimeDb.OperationLogs.Add(Audit.Change(http, $"释放仿真强制：{device.Name} / 测点 #{tagId}", "Tag", tagId, null, new { released }));
                await db.SaveChangesAsync(ct);
                await runtimeDb.SaveChangesAsync(ct);
                return Results.Ok(new { released = released ? 1 : 0 });
            }
            if (await EndpointHelpers.RequireDeviceAsync(http, deviceId, Permission.Control, $"释放全部仿真强制：设备 #{deviceId}") is { } deniedAll) return deniedAll;
            var count = runtime.ReleaseAll(deviceId);
            runtimeDb.OperationLogs.Add(Audit.Change(http, $"释放全部仿真强制：{device.Name}", "Device", deviceId, null, new { released = count }));
            await db.SaveChangesAsync(ct);
            await runtimeDb.SaveChangesAsync(ct);
            return Results.Ok(new { released = count });
        }).WithSummary("Release one or all forced values of a simulated device");

        group.MapPost("/{deviceId:int}/fault", async (
            int deviceId,
            SimulationFaultRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            SimulationRuntime runtime,
            CancellationToken ct) =>
        {
            if (await EndpointHelpers.RequireDeviceAsync(http, deviceId, Permission.Configure, $"仿真故障注入：设备 #{deviceId}") is { } denied) return denied;
            var kind = SimulationFaultKind.None;
            if (!string.IsNullOrWhiteSpace(request.Kind) && !Enum.TryParse(request.Kind, true, out kind))
                return EndpointHelpers.BadRequest("故障类型须为 CommFailure、BadQuality、Frozen 或 None");
            if (kind != SimulationFaultKind.None && request.DurationSec is < 1 or > 3600)
                return EndpointHelpers.BadRequest("故障持续时间须为 1～3600 秒");
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(ct);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null || !device.SimulationEnabled) return EndpointHelpers.BadRequest("该设备未启用仿真");
            runtime.InjectFault(deviceId, kind, request.DurationSec);
            runtimeDb.OperationLogs.Add(Audit.Change(http, $"仿真故障注入：{device.Name} → {kind}", "Device", deviceId, null, new { kind = kind.ToString(), request.DurationSec }));
            await db.SaveChangesAsync(ct);
            await runtimeDb.SaveChangesAsync(ct);
            return Results.Ok(new { fault = kind.ToString(), untilTs = kind == SimulationFaultKind.None ? 0 : ScadaTime.NowMs() + request.DurationSec * 1000L });
        }).WithSummary("Inject or clear a simulated communication fault");

        group.MapDelete("/{deviceId:int}/fault", async (
            int deviceId,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            SimulationRuntime runtime,
            CancellationToken ct) =>
        {
            if (await EndpointHelpers.RequireDeviceAsync(http, deviceId, Permission.Configure, $"清除仿真故障：设备 #{deviceId}") is { } denied) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(ct);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null || !device.SimulationEnabled) return EndpointHelpers.BadRequest("该设备未启用仿真");
            runtime.ClearFault(deviceId);
            runtimeDb.OperationLogs.Add(Audit.Change(http, $"清除仿真故障：{device.Name}", "Device", deviceId, null, new { fault = "None" }));
            await db.SaveChangesAsync(ct);
            await runtimeDb.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithSummary("Clear the simulated communication fault");

        group.MapPost("/{deviceId:int}/reset", async (
            int deviceId,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            SimulationRuntime runtime,
            CancellationToken ct) =>
        {
            if (await EndpointHelpers.RequireDeviceAsync(http, deviceId, Permission.Configure, $"重置仿真：设备 #{deviceId}") is { } denied) return denied;
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(ct);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
            if (device is null || !device.SimulationEnabled) return EndpointHelpers.BadRequest("该设备未启用仿真");
            var released = runtime.ReleaseAll(deviceId);
            runtime.ClearFault(deviceId);
            runtimeDb.OperationLogs.Add(Audit.Change(http, $"重置仿真：{device.Name}", "Device", deviceId, null, new { released }));
            await db.SaveChangesAsync(ct);
            await runtimeDb.SaveChangesAsync(ct);
            return Results.Ok(new { released });
        }).WithSummary("Clear all forced values and faults of a simulated device");
    }

    private static async Task<SimulationView?> LoadAsync(ConfigDbContext db, Scada.Core.Entities.Device device, SimulationRuntime runtime, CancellationToken ct)
    {
        if (!device.SimulationEnabled) return null;
        var tags = await db.Tags.AsNoTracking().Where(x => x.DeviceId == device.Id && x.Kind == TagKind.Physical)
            .OrderBy(x => x.Id).Take(5000).ToListAsync(ct);
        SimulationDeviceOptions options;
        try { options = SimulationOptions.Parse(device); }
        catch (FormatException) { return null; }
        var snapshot = runtime.Snapshot(device, options, tags);
        return new SimulationView(device.Id, device.Name, device.SimulationEnabled, options.Waveform.ToString(), options.PeriodSec,
            options.Min, options.Max, options.NoisePercent, snapshot.Fault.ToString(), snapshot.FaultUntilTs,
            snapshot.Items.Select(item => new SimulationTagView(item.TagId, item.Name, item.DataType.ToString(), item.Value, item.Forced, item.Quality.ToString(), item.Error)).ToList());
    }
}

public sealed record SimulationReleaseRequest(int? TagId);
