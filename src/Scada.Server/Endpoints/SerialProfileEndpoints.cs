using System.IO.Ports;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Scripting;
using Scada.Data;
using Scada.Engine.Calculations;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record SerialProfileRequest(string Name, string? Description, bool Enabled, string Port, int BaudRate, int DataBits,
    SerialParity Parity, SerialStopBits StopBits, string Encoding, string NewLine, int ReadTimeoutMs, int MaxReplyChars,
    bool DtrEnable, bool RtsEnable, long Revision = 0);

public sealed record SerialProfileResponse(int? Id, string Name, string Source, bool Editable, string Description, bool Enabled,
    string Port, int BaudRate, int DataBits, SerialParity Parity, SerialStopBits StopBits, string Encoding, string NewLine,
    int ReadTimeoutMs, int MaxReplyChars, bool DtrEnable, bool RtsEnable, long Revision,
    string? Conflict = null, long? LastUsedAt = null, bool? LastOk = null, string? LastError = null, double? LastElapsedMs = null);

public sealed record SerialProfileListResponse(IReadOnlyList<SerialProfileResponse> Items, int WorkerCount, int EffectiveWorkerCount, bool SerialReady);

// 串口配置是全局资源：一个 COM 口只能有一个启用中的归属（采集通道、设备、追溯扫码或脚本配置）。
public static class SerialProfileEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapSerialProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/serial-profiles").RequireAuthorization(AccessPolicies.GlobalConfigure).AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (IDbContextFactory<ConfigDbContext> factory, IOptions<CSharpScriptOptions> options,
            SerialProfileTelemetry telemetry, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var rows = await db.SerialProfiles.AsNoTracking().OrderBy(row => row.Name).ToListAsync(ct);
            var configured = options.Value.SerialProfiles ?? new();
            var items = rows.Select(Map).ToList();
            foreach (var (name, profile) in configured)
                if (SerialProfileRules.Validate(name, profile) is null && !rows.Any(row => string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)))
                    items.Add(MapConfig(name, profile));

            var channels = await db.Channels.AsNoTracking().Where(row => row.Enabled).ToListAsync(ct);
            var devices = await db.Devices.AsNoTracking()
                .Where(row => row.Enabled && row.ChannelId == null && (row.Protocol == ProtocolType.ModbusRtu || row.Protocol == ProtocolType.ModbusAscii))
                .Select(row => new { row.Name, row.SerialPort }).ToListAsync(ct);
            var jobs = await db.TraceabilityJobs.AsNoTracking()
                .Where(row => row.Enabled && row.SnSource == TraceabilitySnSource.Serial)
                .Select(row => new { row.Name, row.ScannerOptionsJson }).ToListAsync(ct);
            var scannerPorts = new List<(string Name, string Port)>();
            foreach (var job in jobs)
            {
                try
                {
                    var scanner = JsonSerializer.Deserialize<TraceabilityScannerOptions>(job.ScannerOptionsJson, Json);
                    if (scanner is not null) scannerPorts.Add((job.Name, scanner.SerialPort));
                }
                catch (JsonException) { }
            }

            string? ConflictOf(string name, string port, bool active)
            {
                if (!active) return null;
                var other = rows.FirstOrDefault(row => row.Enabled && !string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)
                    && SerialProfileRules.SamePort(row.Port, port));
                if (other is not null) return $"与串口配置「{other.Name}」共用 {port.Trim()}";
                foreach (var (configName, configProfile) in configured)
                    if (!string.Equals(configName, name, StringComparison.OrdinalIgnoreCase) && SerialProfileRules.SamePort(configProfile.Port, port))
                        return $"与配置文件「{configName}」共用 {port.Trim()}";
                var channel = channels.FirstOrDefault(row => ChannelProtocols.UsesSerial(row.Protocol) && SerialProfileRules.SamePort(row.SerialPort, port));
                if (channel is not null) return $"与采集通道「{channel.Name}」共用 {port.Trim()}";
                var device = devices.FirstOrDefault(row => SerialProfileRules.SamePort(row.SerialPort, port));
                if (device is not null) return $"与设备「{device.Name}」共用 {port.Trim()}";
                var scanner = scannerPorts.FirstOrDefault(row => SerialProfileRules.SamePort(row.Port, port));
                return scanner.Name is null ? null : $"与追溯扫码「{scanner.Name}」共用 {port.Trim()}";
            }

            var usages = telemetry.Snapshot();
            items = items.Select(item =>
            {
                var usage = usages.TryGetValue(item.Name, out var last) ? last : null;
                return item with
                {
                    Conflict = ConflictOf(item.Name, item.Port, item.Enabled),
                    LastUsedAt = usage?.AtTs,
                    LastOk = usage?.Ok,
                    LastError = usage?.Error,
                    LastElapsedMs = usage?.ElapsedMs
                };
            }).ToList();
            items.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

            var requested = Math.Clamp(options.Value.WorkerCount, 1, 4);
            var effective = configured.Count > 0 ? 1 : requested;
            var hasActive = configured.Count > 0 || rows.Any(row => row.Enabled);
            return Results.Ok(new SerialProfileListResponse(items, requested, effective, effective == 1 || !hasActive));
        }).Produces<SerialProfileListResponse>();

        group.MapGet("/ports", () =>
        {
            try { return Results.Ok(SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray()); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or PlatformNotSupportedException) { return Results.Ok(Array.Empty<string>()); }
        }).Produces<string[]>();

        group.MapPost("", async (SerialProfileRequest request, IDbContextFactory<ConfigDbContext> factory,
            IOptions<CSharpScriptOptions> options, ISerialProfileCatalog catalog, CancellationToken ct) =>
        {
            var error = await ValidateAsync(request, null, factory, options, ct);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            var entity = new SerialProfileConfig();
            Apply(entity, request);
            db.SerialProfiles.Add(entity);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("串口配置名称已存在"); }
            catalog.Invalidate();
            return Results.Ok(Map(entity));
        }).Produces<SerialProfileResponse>();

        group.MapPut("/{id:int}", async (int id, SerialProfileRequest request, IDbContextFactory<ConfigDbContext> factory,
            IOptions<CSharpScriptOptions> options, ISerialProfileCatalog catalog, CancellationToken ct) =>
        {
            var error = await ValidateAsync(request, id, factory, options, ct);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            var entity = await db.SerialProfiles.FirstOrDefaultAsync(row => row.Id == id, ct);
            if (entity is null) return Results.NotFound();
            if (request.Revision != 0 && request.Revision != entity.Revision) return Results.Conflict(new { message = "配置已被修改，请刷新后重试" });
            Apply(entity, request);
            entity.Revision++;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { return Results.Conflict(new { message = "配置已被修改，请刷新后重试" }); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("串口配置名称已存在"); }
            catalog.Invalidate();
            return Results.Ok(Map(entity));
        }).Produces<SerialProfileResponse>();

        group.MapDelete("/{id:int}", async (int id, IDbContextFactory<ConfigDbContext> factory,
            ISerialProfileCatalog catalog, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var entity = await db.SerialProfiles.FirstOrDefaultAsync(row => row.Id == id, ct);
            if (entity is null) return Results.NotFound();
            db.SerialProfiles.Remove(entity);
            await db.SaveChangesAsync(ct);
            catalog.Invalidate();
            return Results.NoContent();
        }).Produces(StatusCodes.Status204NoContent);
    }

    private static async Task<string?> ValidateAsync(SerialProfileRequest request, int? id, IDbContextFactory<ConfigDbContext> factory,
        IOptions<CSharpScriptOptions> options, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        var profile = new SerialProfile
        {
            Port = request.Port?.Trim() ?? "", BaudRate = request.BaudRate, DataBits = request.DataBits,
            Parity = request.Parity, StopBits = request.StopBits, Encoding = request.Encoding?.Trim() ?? "",
            NewLine = request.NewLine ?? "", ReadTimeoutMs = request.ReadTimeoutMs, MaxReplyChars = request.MaxReplyChars,
            DtrEnable = request.DtrEnable, RtsEnable = request.RtsEnable
        };
        if (SerialProfileRules.Validate(name, profile) is { } error) return error;
        var configured = options.Value.SerialProfiles ?? new();
        if (configured.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)))
            return $"名称与配置文件中的串口配置「{name}」重复";
        if (configured.Any(item => SerialProfileRules.SamePort(item.Value.Port, profile.Port)))
            return $"串口 {profile.Port} 已被配置文件中的串口配置占用";
        if (request.Enabled)
        {
            var requested = Math.Clamp(options.Value.WorkerCount, 1, 4);
            var effective = configured.Count > 0 ? 1 : requested;
            if (effective != 1) return "启用串口配置需要 Scada:CSharpScripts:WorkerCount=1，修改后重启服务";
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.SerialProfiles.AsNoTracking().ToListAsync(ct);
        if (rows.Any(row => row.Id != id && string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase)))
            return $"串口配置名称「{name}」已存在";
        if (!request.Enabled) return null;
        var busy = rows.FirstOrDefault(row => row.Enabled && row.Id != id && SerialProfileRules.SamePort(row.Port, profile.Port));
        if (busy is not null) return $"串口 {profile.Port} 已被串口配置「{busy.Name}」占用";

        var channels = await db.Channels.AsNoTracking().Where(row => row.Enabled).ToListAsync(ct);
        var channel = channels.FirstOrDefault(row => ChannelProtocols.UsesSerial(row.Protocol) && SerialProfileRules.SamePort(row.SerialPort, profile.Port));
        if (channel is not null) return $"串口 {profile.Port} 已被采集通道「{channel.Name}」占用";

        var devices = await db.Devices.AsNoTracking()
            .Where(row => row.Enabled && row.ChannelId == null && (row.Protocol == ProtocolType.ModbusRtu || row.Protocol == ProtocolType.ModbusAscii))
            .Select(row => new { row.Name, row.SerialPort }).ToListAsync(ct);
        var device = devices.FirstOrDefault(row => SerialProfileRules.SamePort(row.SerialPort, profile.Port));
        if (device is not null) return $"串口 {profile.Port} 已被设备「{device.Name}」独占";

        var jobs = await db.TraceabilityJobs.AsNoTracking()
            .Where(row => row.Enabled && row.SnSource == TraceabilitySnSource.Serial)
            .Select(row => new { row.Name, row.ScannerOptionsJson }).ToListAsync(ct);
        foreach (var job in jobs)
        {
            TraceabilityScannerOptions? scanner = null;
            try { scanner = JsonSerializer.Deserialize<TraceabilityScannerOptions>(job.ScannerOptionsJson, Json); }
            catch (JsonException) { }
            if (scanner is not null && SerialProfileRules.SamePort(scanner.SerialPort, profile.Port))
                return $"串口 {profile.Port} 已被追溯扫码任务「{job.Name}」占用";
        }
        return null;
    }

    private static void Apply(SerialProfileConfig entity, SerialProfileRequest request)
    {
        entity.Name = request.Name.Trim(); entity.Description = request.Description?.Trim() ?? "";
        entity.Enabled = request.Enabled; entity.Port = request.Port.Trim();
        entity.BaudRate = request.BaudRate; entity.DataBits = request.DataBits;
        entity.Parity = request.Parity; entity.StopBits = request.StopBits;
        entity.Encoding = NormalizeEncoding(request.Encoding); entity.NewLine = NormalizeNewLine(request.NewLine);
        entity.ReadTimeoutMs = request.ReadTimeoutMs; entity.MaxReplyChars = request.MaxReplyChars;
        entity.DtrEnable = request.DtrEnable; entity.RtsEnable = request.RtsEnable;
    }

    private static string NormalizeEncoding(string? value) => (value ?? "").Trim().ToLowerInvariant() is "ascii" or "us-ascii" ? "ascii" : "utf-8";
    private static string NormalizeNewLine(string? value) => string.IsNullOrEmpty(value) ? "\r\n" : value;

    private static SerialProfileResponse Map(SerialProfileConfig entity) => new(entity.Id, entity.Name, "database", true,
        entity.Description, entity.Enabled, entity.Port, entity.BaudRate, entity.DataBits, entity.Parity, entity.StopBits,
        NormalizeEncoding(entity.Encoding), NormalizeNewLine(entity.NewLine), entity.ReadTimeoutMs, entity.MaxReplyChars, entity.DtrEnable, entity.RtsEnable, entity.Revision);

    private static SerialProfileResponse MapConfig(string name, SerialProfile profile) => new(null, name, "config", false, "",
        true, profile.Port, profile.BaudRate, profile.DataBits, profile.Parity, profile.StopBits, NormalizeEncoding(profile.Encoding),
        NormalizeNewLine(profile.NewLine), profile.ReadTimeoutMs, profile.MaxReplyChars, profile.DtrEnable, profile.RtsEnable, 0);
}
