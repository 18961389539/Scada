using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Security;
using Scada.Data;
using Scada.Engine.Traceability;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record TraceabilityJobRequest(string Name, bool Enabled, int TriggerTagId, TraceabilitySnSource SnSource,
    TraceabilitySnFormat SnFormat, List<int>? SnTagIds, List<TraceabilityField>? Fields, int ScanExpirySeconds = 300, long Revision = 0,
    TraceabilityScannerOptions? Scanner = null);
public sealed record TraceabilityScanRequest(string Sn);
public sealed record TraceabilityJobResponse(int Id, string Name, bool Enabled, int TriggerTagId, TraceabilitySnSource SnSource,
    TraceabilitySnFormat SnFormat, IReadOnlyList<int> SnTagIds, IReadOnlyList<TraceabilityField> Fields, int ScanExpirySeconds,
    long Revision, bool CanConfigure, bool CanScan, TraceabilityStatus Status, TraceabilityScannerOptions Scanner, TraceabilityScannerStatus ScannerStatus);
public sealed record TraceabilityRecordResponse(long Id, int JobId, string JobName, string Sn, DateTimeOffset CapturedAt,
    bool Acknowledged, IReadOnlyList<TraceabilityValue> Values);

public static class TraceabilityEndpoints
{
    public static void MapTraceabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/traceability").RequireAuthorization().AddEndpointFilter<AuditFailuresFilter>();
        group.MapGet("/jobs", async (HttpContext http, IDbContextFactory<ConfigDbContext> factory, TraceabilityService service, TraceabilityScannerHost scanners, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var jobs = await db.TraceabilityJobs.AsNoTracking().OrderBy(job => job.Name).ToListAsync(ct);
            return TypedResults.Ok(jobs.Where(job => access.CanTag(Permission.View, job.TriggerTagId)).Select(job => View(job, service, scanners, access)).ToList());
        });
        group.MapPost("/jobs", (TraceabilityJobRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, TraceabilityService service, TraceabilityScannerHost scanners, CancellationToken ct)
            => SaveAsync(null, request, http, factory, service, scanners, ct)).RequireAuthorization(AccessPolicies.Configure);
        group.MapPut("/jobs/{id:int}", (int id, TraceabilityJobRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, TraceabilityService service, TraceabilityScannerHost scanners, CancellationToken ct)
            => SaveAsync(id, request, http, factory, service, scanners, ct)).RequireAuthorization(AccessPolicies.Configure);
        group.MapPost("/jobs/{id:int}/scan", async Task<IResult> (int id, TraceabilityScanRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, TraceabilityService service, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var job = await db.TraceabilityJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == id, ct);
            if (job is null) return TypedResults.NotFound();
            if (!(await http.GetAccessAsync()).CanTag(Permission.Control, job.TriggerTagId)) return await Audit.ForbidAsync(http, "Bind traceability SN", "Control permission is required", "TraceabilityJob", id);
            try
            {
                var status = await service.ScanAsync(id, request.Sn, ct);
                db.OperationLogs.Add(Audit.Change(http, "Bind traceability SN", "TraceabilityJob", id, null, new { status.PendingSn, status.ScanExpiresTs }));
                await db.SaveChangesAsync(ct);
                return TypedResults.Ok(status);
            }
            catch (InvalidOperationException ex) { return TypedResults.Conflict(new { message = ex.Message }); }
        }).RequireAuthorization(AccessPolicies.Control);
        group.MapDelete("/jobs/{id:int}/scan", async Task<IResult> (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, TraceabilityService service, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var job = await db.TraceabilityJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == id, ct);
            if (job is null) return TypedResults.NotFound();
            if (!(await http.GetAccessAsync()).CanTag(Permission.Control, job.TriggerTagId)) return await Audit.ForbidAsync(http, "Clear traceability SN", "Control permission is required", "TraceabilityJob", id);
            await service.ClearScanAsync(id, ct);
            db.OperationLogs.Add(Audit.Change(http, "Clear traceability SN", "TraceabilityJob", id, null, null));
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(AccessPolicies.Control);
        group.MapGet("/records", async Task<IResult> (string? sn, int? jobId, long? from, long? to, int? page, int? pageSize,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, CancellationToken ct) =>
        {
            if (sn?.Length > 200 || from > to) return TypedResults.BadRequest(new { message = "Invalid SN or time range." });
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var areas = (await db.Areas.AsNoTracking().ToListAsync(ct)).Where(area => access.Has(Permission.View, area.Id)).Select(area => area.Id).ToArray();
            var query = db.TraceabilityRecords.AsNoTracking().Where(record => areas.Contains(record.AreaId));
            if (!string.IsNullOrWhiteSpace(sn)) query = query.Where(record => record.Sn == sn.Trim());
            if (jobId is int id) query = query.Where(record => record.JobId == id);
            if (from is long start) query = query.Where(record => record.Ts >= start);
            if (to is long end) query = query.Where(record => record.Ts <= end);
            var currentPage = Math.Clamp(page ?? 1, 1, 100000);
            var size = Math.Clamp(pageSize ?? 50, 1, 200);
            var total = await query.CountAsync(ct);
            var records = await query.OrderByDescending(record => record.Ts).ThenByDescending(record => record.Id).Skip((currentPage - 1) * size).Take(size).ToListAsync(ct);
            return TypedResults.Ok(new { total, page = currentPage, pageSize = size, items = records.Select(RecordView).ToList() });
        });
    }

    private static async Task<IResult> SaveAsync(int? id, TraceabilityJobRequest request, HttpContext http,
        IDbContextFactory<ConfigDbContext> factory, TraceabilityService service, TraceabilityScannerHost scanners, CancellationToken ct)
    {
        if (request.SnTagIds is null || request.Fields is null || request.Fields.Any(field => field is null))
            return TypedResults.BadRequest(new { message = "SN registers and data fields are required." });
        return await service.ExclusiveAsync<IResult>(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var access = await http.GetAccessAsync();
            var job = id is int existingId ? await db.TraceabilityJobs.SingleOrDefaultAsync(job => job.Id == existingId, ct) : new TraceabilityJob();
            if (job is null) return TypedResults.NotFound();
            if (id.HasValue && !access.CanTag(Permission.Configure, job.TriggerTagId)) return await Audit.ForbidAsync(http, "Configure traceability", "Configuration permission is required", "TraceabilityJob", id);
            if (!access.CanTag(Permission.Configure, request.TriggerTagId) || !access.CanTag(Permission.Control, request.TriggerTagId))
                return await Audit.ForbidAsync(http, "Configure traceability", "Configuration and control permission are required for the trigger", "Tag", request.TriggerTagId);
            if (id.HasValue && job.Revision != request.Revision) return TypedResults.Conflict(new { message = "Configuration changed; reload before saving." });
            if (id.HasValue && await db.TraceabilityAttempts.AnyAsync(attempt => attempt.JobId == job.Id && attempt.ClosedTs == null, ct))
                return TypedResults.Conflict(new { message = "A capture is awaiting acknowledgement. Its configuration cannot be changed." });
            var before = id.HasValue ? View(job, service, scanners, access) : null;
            job.Name = request.Name?.Trim() ?? ""; job.Enabled = request.Enabled; job.TriggerTagId = request.TriggerTagId;
            job.SnSource = request.SnSource; job.SnFormat = request.SnFormat; job.ScanExpirySeconds = request.ScanExpirySeconds;
            job.SnTagIdsJson = JsonSerializer.Serialize(request.SnTagIds, TraceabilityService.Json);
            job.FieldsJson = JsonSerializer.Serialize(request.Fields, TraceabilityService.Json);
            job.ScannerOptionsJson = JsonSerializer.Serialize(request.Scanner ?? new(), TraceabilityService.Json);
            var errors = service.Validate(job);
            if (errors.Count > 0) return TypedResults.BadRequest(new { message = string.Join(" ", errors) });
            if (request.SnTagIds.Concat(request.Fields.Select(field => field.TagId)).Any(tagId => !access.CanTag(Permission.View, tagId)))
                return await Audit.ForbidAsync(http, "Configure traceability", "View permission is required for every data point", "TraceabilityJob", id);
            if (await db.TraceabilityJobs.AnyAsync(other => other.TriggerTagId == job.TriggerTagId && other.Id != job.Id, ct))
                return TypedResults.Conflict(new { message = "The trigger is already assigned to another traceability job." });
            if (job.Enabled && job.SnSource is TraceabilitySnSource.Tcp or TraceabilitySnSource.Serial)
            {
                var options = TraceabilityService.ScannerOptions(job);
                var others = await db.TraceabilityJobs.AsNoTracking().Where(other => other.Enabled && other.Id != job.Id && other.SnSource == job.SnSource).ToListAsync(ct);
                foreach (var other in others)
                {
                    var existing = TraceabilityService.ScannerOptions(other);
                    var conflict = job.SnSource == TraceabilitySnSource.Serial
                        ? string.Equals(options.SerialPort, existing.SerialPort, StringComparison.OrdinalIgnoreCase)
                        : options.Port == existing.Port && options.TcpMode == existing.TcpMode &&
                            (options.TcpMode == TraceabilityTcpMode.Server || string.Equals(options.Host, existing.Host, StringComparison.OrdinalIgnoreCase));
                    if (conflict) return TypedResults.Conflict(new { message = "The scanner connection is already assigned to another enabled job." });
                }
                if (job.SnSource == TraceabilitySnSource.Serial && await db.Devices.AnyAsync(device => device.Enabled &&
                    (device.Protocol == ProtocolType.ModbusRtu || device.Protocol == ProtocolType.ModbusAscii) && device.SerialPort.ToUpper() == options.SerialPort.ToUpper(), ct))
                    return TypedResults.Conflict(new { message = "The serial port is already used by an enabled PLC device." });
            }
            job.Revision = id.HasValue ? job.Revision + 1 : 1;
            if (!id.HasValue) db.TraceabilityJobs.Add(job);
            db.OperationLogs.Add(Audit.Change(http, "Configure traceability", "TraceabilityJob", id, before, View(job, service, scanners, access)));
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return TypedResults.Conflict(new { message = "Configuration changed; reload before saving." }); }
            service.ResetScan(job.Id);
            var response = View(job, service, scanners, access);
            return id.HasValue ? TypedResults.Ok(response) : TypedResults.Created($"/api/traceability/jobs/{job.Id}", response);
        }, ct);
    }

    private static TraceabilityJobResponse View(TraceabilityJob job, TraceabilityService service, TraceabilityScannerHost scanners, AccessContext access) => new(
        job.Id, job.Name, job.Enabled, job.TriggerTagId, job.SnSource, job.SnFormat, TraceabilityService.SnTags(job), TraceabilityService.Fields(job),
        job.ScanExpirySeconds, job.Revision, access.CanTag(Permission.Configure, job.TriggerTagId) && access.CanTag(Permission.Control, job.TriggerTagId),
        access.CanTag(Permission.Control, job.TriggerTagId), service.Status(job.Id), TraceabilityService.ScannerOptions(job), scanners.Status(job.Id));

    private static TraceabilityRecordResponse RecordView(TraceabilityRecord record) => new(record.Id, record.JobId, record.JobName, record.Sn,
        DateTimeOffset.FromUnixTimeMilliseconds(record.Ts), record.Acknowledged, TraceabilityService.Values(record));
}