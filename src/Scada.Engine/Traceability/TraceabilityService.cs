using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;

namespace Scada.Engine.Traceability;

public sealed record TraceabilityStatus(int JobId, string State, string? PendingSn, long? ScanExpiresTs, int? Reply, string? Error);

public sealed class TraceabilityService(
    IDbContextFactory<ConfigDbContext> factory, IConfigSnapshot config, IRealtimeCache cache,
    ICollectorControl collector, INodeRole role, ILogger<TraceabilityService> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, (string Sn, long ExpiresTs, long Revision)> _scans = new();
    private readonly ConcurrentDictionary<int, TraceabilityStatus> _statuses = new();

    public static List<int> SnTags(TraceabilityJob job) => JsonSerializer.Deserialize<List<int>>(job.SnTagIdsJson, Json) ?? [];
    public static TraceabilityScannerOptions ScannerOptions(TraceabilityJob job) => JsonSerializer.Deserialize<TraceabilityScannerOptions>(job.ScannerOptionsJson, Json) ?? new();
    public static List<TraceabilityField> Fields(TraceabilityJob job) => JsonSerializer.Deserialize<List<TraceabilityField>>(job.FieldsJson, Json) ?? [];
    public static List<TraceabilityValue> Values(TraceabilityRecord record) => JsonSerializer.Deserialize<List<TraceabilityValue>>(record.ValuesJson, Json) ?? [];

    public TraceabilityStatus Status(int id)
    {
        var status = _statuses.GetValueOrDefault(id) ?? new(id, "idle", null, null, null, null);
        return status.ScanExpiresTs <= ScadaTime.NowMs() ? status with { PendingSn = null, ScanExpiresTs = null } : status;
    }

    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await action(); }
        finally { _gate.Release(); }
    }

    public Task<TraceabilityStatus> ScanAsync(int jobId, string sn, CancellationToken ct) => BindScanAsync(jobId, sn, TraceabilitySnSource.WebScan, null, ct);

    public Task<TraceabilityStatus> ReceiveScannerAsync(int jobId, string sn, TraceabilitySnSource source, long revision, CancellationToken ct)
    {
        if (source is not (TraceabilitySnSource.Tcp or TraceabilitySnSource.Serial)) throw new InvalidOperationException("Invalid scanner source.");
        return BindScanAsync(jobId, sn, source, revision, ct);
    }

    private Task<TraceabilityStatus> BindScanAsync(int jobId, string sn, TraceabilitySnSource source, long? revision, CancellationToken ct) => ExclusiveAsync(async () =>
    {
        if (!role.IsActive) throw new InvalidOperationException(role.StandbyMessage);
        await using var db = await factory.CreateDbContextAsync(ct);
        var job = await db.TraceabilityJobs.SingleOrDefaultAsync(job => job.Id == jobId, ct)
            ?? throw new InvalidOperationException("Traceability job does not exist.");
        if (!job.Enabled || job.SnSource != source || revision.HasValue && job.Revision != revision.Value)
            throw new InvalidOperationException("This job does not accept scans from this source or configuration revision.");
        if (await db.TraceabilityAttempts.AnyAsync(attempt => attempt.JobId == jobId && attempt.ClosedTs == null, ct))
            throw new InvalidOperationException("The previous capture is awaiting acknowledgement.");
        if (_scans.TryGetValue(jobId, out var previous) && previous.ExpiresTs > ScadaTime.NowMs() && previous.Revision == job.Revision)
            throw new InvalidOperationException("A scanned SN is already waiting. Clear it before replacing it.");
        var value = TraceabilityCapture.ValidateSn(sn);
        var expires = ScadaTime.NowMs() + job.ScanExpirySeconds * 1000L;
        _scans[jobId] = (value, expires, job.Revision);
        var status = new TraceabilityStatus(jobId, "ready", value, expires, null, null);
        _statuses[jobId] = status;
        return status;
    }, ct);

    public Task<bool> ClearScanAsync(int id, CancellationToken ct) => ExclusiveAsync(() =>
    {
        ResetScan(id);
        return Task.FromResult(true);
    }, ct);

    public void ResetScan(int id)
    {
        _scans.Remove(id);
        var status = Status(id);
        _statuses[id] = status with { PendingSn = null, ScanExpiresTs = null, State = status.State == "ready" ? "idle" : status.State };
    }

    public List<string> Validate(TraceabilityJob job)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(job.Name) || job.Name.Length > 100) errors.Add("Name must contain 1 to 100 characters.");
        if (!Enum.IsDefined(job.SnSource) || !Enum.IsDefined(job.SnFormat)) errors.Add("Invalid SN source or format.");
        if (job.ScanExpirySeconds is < 5 or > 3600) errors.Add("Scan expiry must be between 5 and 3600 seconds.");
        if (job.SnSource is TraceabilitySnSource.Tcp or TraceabilitySnSource.Serial)
        {
            TraceabilityScannerOptions options;
            try { options = ScannerOptions(job); }
            catch (JsonException) { errors.Add("Invalid scanner configuration."); return errors; }
            if (!Enum.IsDefined(options.Terminator) || !Enum.IsDefined(options.Encoding)) errors.Add("Invalid scanner terminator or encoding.");
            if (job.SnSource == TraceabilitySnSource.Tcp)
            {
                if (!Enum.IsDefined(options.TcpMode) || string.IsNullOrWhiteSpace(options.Host) || options.Host.Length > 253 || options.Host.Any(char.IsWhiteSpace) || options.Port is < 1 or > 65535)
                    errors.Add("Configure a TCP host and port between 1 and 65535.");
                if (options.TcpMode == TraceabilityTcpMode.Server && !System.Net.IPAddress.TryParse(options.Host, out _))
                    errors.Add("TCP listen address must be a local IP address.");
            }
            else if (string.IsNullOrWhiteSpace(options.SerialPort) || options.SerialPort.Length > 128 || options.BaudRate is < 300 or > 921600 || options.DataBits is < 5 or > 8 || !Enum.IsDefined(options.Parity) || !Enum.IsDefined(options.StopBits))
                errors.Add("Invalid scanner serial port settings.");
        }
        if (!config.TryGetTag(job.TriggerTagId, out var trigger) || trigger.Kind != TagKind.Physical || trigger.DeviceId is not int deviceId || !config.TryGetDevice(deviceId, out var device))
        { errors.Add("Trigger must be a physical PLC point."); return errors; }
        if (device.GatewayId is not null || device.Protocol is not (ProtocolType.ModbusTcp or ProtocolType.ModbusRtu or ProtocolType.ModbusAscii or ProtocolType.ModbusRtuOverTcp or ProtocolType.SiemensS7 or ProtocolType.MitsubishiMc or ProtocolType.OmronFins))
            errors.Add("Traceability requires a directly connected Modbus (TCP/RTU/ASCII/RTU-over-TCP), S7, Mitsubishi MC or Omron FINS PLC.");
        if (!trigger.Writable || trigger.DataType == TagDataType.Bool || trigger.Scale != 1 || trigger.Offset != 0 ||
            !CommandValuePolicy.TryGetRawValue(trigger, 1, out _, out _) || !CommandValuePolicy.TryGetRawValue(trigger, 2, out _, out _) || !CommandValuePolicy.TryGetRawValue(trigger, 3, out _, out _))
            errors.Add("Trigger must allow unscaled numeric values 1, 2 and 3 and be writable.");
        var snTags = SnTags(job);
        var fields = Fields(job);
        if (fields.Count is < 1 or > 64 || fields.Any(field => string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 100) || fields.Select(field => field.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count)
            errors.Add("Configure 1 to 64 data fields with unique names.");
        var outcomeMappings = fields.Select(field => field.OutcomeMapping).OfType<TraceabilityOutcomeMapping>().ToArray();
        if (outcomeMappings.Length > 1 || outcomeMappings.Any(mapping =>
            !double.IsFinite(mapping.PassValue) || !double.IsFinite(mapping.FailValue) || mapping.PassValue == mapping.FailValue))
            errors.Add("Configure at most one inspection result field with distinct, finite pass and fail values.");
        if (job.SnSource != TraceabilitySnSource.Plc && snTags.Count != 0) errors.Add("Scanner sources and PLC SN registers are mutually exclusive.");
        if (job.SnSource == TraceabilitySnSource.Plc && (snTags.Count is < 1 or > 100 || snTags.Distinct().Count() != snTags.Count || job.SnFormat == TraceabilitySnFormat.Numeric && snTags.Count != 1))
            errors.Add("Configure one numeric SN point or 1 to 100 ASCII registers in byte order.");
        foreach (var id in fields.Select(field => field.TagId).Concat(snTags))
        {
            if (id == trigger.Id || !config.TryGetTag(id, out var tag) || tag.Kind != TagKind.Physical || tag.DeviceId != deviceId)
            { errors.Add($"Point #{id} must belong to the trigger PLC and cannot be the trigger itself."); continue; }
            if (snTags.Contains(id) && job.SnFormat != TraceabilitySnFormat.Numeric && (tag.DataType != TagDataType.UInt16 || tag.Scale != 1 || tag.Offset != 0))
                errors.Add($"ASCII SN point #{id} must be an unscaled UInt16 register.");
        }
        return errors;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ProcessOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Traceability polling failed"); }
            try { if (!await timer.WaitForNextTickAsync(stoppingToken)) break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task ProcessOnceAsync(CancellationToken ct)
    {
        if (!role.IsActive) return;
        await ExclusiveAsync(async () =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var jobs = await db.TraceabilityJobs.AsNoTracking().ToListAsync(ct);
            foreach (var job in jobs)
            {
                if (!role.IsActive) break;
                try { await ProcessJobAsync(job, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    var status = Status(job.Id);
                    _statuses[job.Id] = status with { State = "error", Error = ex.Message };
                    logger.LogWarning(ex, "Traceability job {JobId} failed", job.Id);
                }
            }
            return true;
        }, ct);
    }

    private async Task ProcessJobAsync(TraceabilityJob job, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var pending = await db.TraceabilityAttempts.SingleOrDefaultAsync(attempt => attempt.JobId == job.Id && attempt.ClosedTs == null, ct);
        if (!job.Enabled && pending is null) return;
        if (!config.TryGetTag(job.TriggerTagId, out var trigger) || trigger.DeviceId is not int deviceId || !config.TryGetDevice(deviceId, out var device))
            throw new InvalidOperationException("Trigger PLC is unavailable.");
        if (pending is not null)
        {
            if (pending.Reply == 0)
            {
                pending.Reply = 3; pending.Error = "Capture was interrupted before commit; no automatic recapture.";
                await db.SaveChangesAsync(ct);
            }
            await ReplyAsync(db, job, trigger, pending, ct);
            return;
        }
        var current = cache.Get(trigger.Id);
        var now = ScadaTime.NowMs();
        if (current is null || current.Quality != TagQuality.Good || current.Ts < now - Math.Max(5000L, (long)device.PollIntervalMs * 3))
        {
            _statuses[job.Id] = Status(job.Id) with { State = "offline", Error = "Trigger PLC is offline or its value is stale." };
            return;
        }
        if (current.Value != 1) return;
        var validation = Validate(job);
        if (validation.Count > 0) throw new InvalidOperationException(string.Join(" ", validation));
        var scan = _scans.Remove(job.Id, out var scanned) && scanned.ExpiresTs > now && scanned.Revision == job.Revision ? scanned.Sn : null;
        var attempt = new TraceabilityAttempt { Id = Guid.NewGuid(), JobId = job.Id, StartedTs = now };
        db.TraceabilityAttempts.Add(attempt);
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failure = await collector.WriteAsync(deviceId, trigger, 3, ct);
            _statuses[job.Id] = new(job.Id, failure.Success ? "failed" : "awaitingReply", null, null, 3, ex.Message);
            logger.LogError(ex, "Cannot start traceability capture because its database is unavailable");
            return;
        }
        _statuses[job.Id] = new(job.Id, "capturing", null, null, null, null);
        var result = await TraceabilityCapture.RunAsync(async token =>
        {
            var fields = Fields(job);
            var snIds = job.SnSource == TraceabilitySnSource.Plc ? SnTags(job) : [];
            var ids = fields.Select(field => field.TagId).Concat(snIds).Append(trigger.Id).Distinct().ToArray();
            var values = (await collector.ReadAsync(deviceId, ids, token)).ToDictionary(value => value.TagId);
            foreach (var id in ids)
                if (!values.TryGetValue(id, out var value) || value.Quality != TagQuality.Good || !double.IsFinite(value.Value))
                    throw new IOException($"Point #{id} is missing or invalid.");
            if (values[trigger.Id].Value != 1) throw new IOException("PLC withdrew the capture request.");
            var sn = job.SnSource == TraceabilitySnSource.Plc
                ? TraceabilityCapture.ReadSn(snIds.Select(id => values[id]).ToArray(), job.SnFormat)
                : TraceabilityCapture.ValidateSn(scan);
            var payload = fields.Select(field => new TraceabilityValue(field.Name, field.TagId, values[field.TagId].Value,
                config.TryGetTag(field.TagId, out var tag) ? tag.Unit : null, field.OutcomeMapping?.Evaluate(values[field.TagId].Value))).ToArray();
            return new TraceabilityRecord { CycleId = attempt.Id, JobId = job.Id, JobName = job.Name, AreaId = device.AreaId,
                Sn = sn, Ts = ScadaTime.NowMs(), ValuesJson = JsonSerializer.Serialize(payload, Json),
                Outcome = payload.SingleOrDefault(value => value.Outcome.HasValue)?.Outcome ?? TraceabilityOutcome.NotEvaluated };
        }, async (record, token) =>
        {
            db.TraceabilityRecords.Add(record);
            attempt.Reply = 2;
            await db.SaveChangesAsync(token);
        }, async (code, token) =>
        {
            if (code == 3)
            {
                foreach (var entry in db.ChangeTracker.Entries<TraceabilityRecord>().Where(entry => entry.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
                attempt.Reply = 3; attempt.Error = "Acquisition or database commit failed.";
                try { await db.SaveChangesAsync(token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Cannot persist the failed capture before replying 3"); }
            }
            await ReplyAsync(db, job, trigger, attempt, token);
        }, ct);
        if (result.Error is not null)
        {
            attempt.Error = result.Error;
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "Failed to persist traceability failure"); }
        }
        _statuses[job.Id] = new(job.Id, result.ReplyError is not null ? "awaitingReply" : result.Reply == 2 ? "completed" : "failed",
            null, null, result.Reply, result.ReplyError ?? result.Error);
    }

    private async Task ReplyAsync(ConfigDbContext db, TraceabilityJob job, Tag trigger, TraceabilityAttempt attempt, CancellationToken ct)
    {
        if (!role.IsActive) throw new InvalidOperationException(role.StandbyMessage);
        var feedback = (await collector.ReadAsync(trigger.DeviceId!.Value, [trigger.Id], ct)).Single();
        if (feedback.Quality != TagQuality.Good) throw new IOException("Cannot verify the trigger before replying.");
        if (feedback.Value != attempt.Reply && feedback.Value != 1)
            throw new IOException("Trigger changed before acknowledgement; pending result requires operator verification.");
        if (feedback.Value == 1)
        {
            var result = await collector.WriteAsync(trigger.DeviceId.Value, trigger, attempt.Reply, ct);
            if (!result.Success) throw new IOException(result.Message);
        }
        attempt.Acknowledged = true; attempt.ClosedTs = ScadaTime.NowMs();
        var record = await db.TraceabilityRecords.SingleOrDefaultAsync(record => record.CycleId == attempt.Id, ct);
        if (record is not null) record.Acknowledged = true;
        await db.SaveChangesAsync(ct);
        _statuses[job.Id] = new(job.Id, attempt.Reply == 2 ? "completed" : "failed", null, null, attempt.Reply, attempt.Error);
    }
}