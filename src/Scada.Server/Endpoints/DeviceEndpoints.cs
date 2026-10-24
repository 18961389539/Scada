using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Services;
using Scada.Collector;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record DeviceRequest(
    string Name,
    ProtocolType Protocol,
    string Host,
    int Port,
    byte StationNumber,
    ByteOrder ByteOrder,
    int PollIntervalMs,
    int TimeoutMs,
    bool Enabled,
    string SerialPort = "COM1", int BaudRate = 9600, int DataBits = 8,
    SerialParity Parity = SerialParity.None, SerialStopBits StopBits = SerialStopBits.One,
    S7Cpu Cpu = S7Cpu.S71200, short Rack = 0, short Slot = 1,
    string OpcEndpointUrl = "opc.tcp://localhost:4840", OpcSecurityMode OpcSecurityMode = OpcSecurityMode.SignAndEncrypt,
    OpcSecurityPolicy OpcSecurityPolicy = OpcSecurityPolicy.Basic256Sha256, string OpcUserName = "", string? OpcPassword = null, bool ClearOpcPassword = false, int? AreaId = null);


public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var devices = await db.Devices.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            return Results.Ok(devices.Where(x => access.Has(Permission.View, x.AreaId)).ToList());
        });

        group.MapGet("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return device is null || !access.Has(Permission.View, device.AreaId) ? Results.NotFound() : Results.Ok(device);
        });

        group.MapPost("", async (
            DeviceRequest request,
            HttpContext http,
            IDeviceCredentials credentials,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var areaId = request.AreaId ?? Area.DefaultId;
            if (await EndpointHelpers.RequireAreaAsync(http, areaId, Permission.Configure, $"创建设备：{request.Name}") is { } denied) return denied;
            var device = new Device
            {
                AreaId = areaId,
                Name = request.Name?.Trim() ?? "",
                Protocol = request.Protocol,
                Host = request.Host?.Trim() ?? "",
                Port = request.Port,
                StationNumber = request.StationNumber,
                ByteOrder = request.ByteOrder,
                PollIntervalMs = request.PollIntervalMs,
                TimeoutMs = request.TimeoutMs,
                Enabled = request.Enabled,
                DisabledSinceTs = request.Enabled ? 0 : ScadaTime.NowMs(),
                SerialPort = request.SerialPort?.Trim() ?? "", BaudRate = request.BaudRate, DataBits = request.DataBits,
                Parity = request.Parity, StopBits = request.StopBits, Cpu = request.Cpu, Rack = request.Rack, Slot = request.Slot
            };
            var credentialError = ApplyOpcSettings(device, request, credentials);
            if (credentialError is not null) return EndpointHelpers.BadRequest(credentialError);
            var error = EndpointHelpers.ValidateDevice(device);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (!await db.Areas.AnyAsync(x => x.Id == areaId, cancellationToken)) return EndpointHelpers.BadRequest("区域不存在");
            error = await ValidateSerialBusAsync(db, device, cancellationToken);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.Devices.AnyAsync(x => x.Name == device.Name, cancellationToken))
            {
                return EndpointHelpers.BadRequest("设备名称已存在");
            }

            db.Devices.Add(device);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                db.OperationLogs.Add(Audit.Change(http, $"创建设备：{device.Name}", "Device", device.Id, null, device));
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return EndpointHelpers.BadRequest("设备名称已存在");
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.Created($"/api/devices/{device.Id}", device);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id,
            DeviceRequest request,
            HttpContext http,
            IDeviceCredentials credentials,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (device is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireDeviceAsync(http, id, Permission.Configure, $"修改设备 #{id}") is { } denied) return denied;
            if (request.AreaId is int newArea && newArea != device.AreaId)
            {
                if (await EndpointHelpers.RequireAreaAsync(http, newArea, Permission.Configure, $"移动设备 #{id} 到区域 #{newArea}") is { } deniedMove) return deniedMove;
                if (!await db.Areas.AnyAsync(x => x.Id == newArea, cancellationToken)) return EndpointHelpers.BadRequest("区域不存在");
            }

            var before = device.Clone();
            if (request.AreaId is int targetArea) device.AreaId = targetArea;
            var name = request.Name?.Trim() ?? "";
            var host = request.Host?.Trim() ?? "";
            if (await db.Devices.AnyAsync(x => x.Id != id && x.Name == name, cancellationToken))
            {
                return EndpointHelpers.BadRequest("设备名称已存在");
            }

            device.Name = name;
            device.Protocol = request.Protocol;
            device.Host = host;
            device.Port = request.Port;
            device.StationNumber = request.StationNumber;
            device.ByteOrder = request.ByteOrder;
            device.PollIntervalMs = request.PollIntervalMs;
            device.TimeoutMs = request.TimeoutMs;
            if (device.Enabled && !request.Enabled) device.DisabledSinceTs = ScadaTime.NowMs();
            device.Enabled = request.Enabled;
            device.SerialPort = request.SerialPort?.Trim() ?? ""; device.BaudRate = request.BaudRate; device.DataBits = request.DataBits;
            device.Parity = request.Parity; device.StopBits = request.StopBits; device.Cpu = request.Cpu; device.Rack = request.Rack; device.Slot = request.Slot;
            var credentialError = ApplyOpcSettings(device, request, credentials);
            if (credentialError is not null) return EndpointHelpers.BadRequest(credentialError);

            var error = EndpointHelpers.ValidateDevice(device);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            error = await ValidateSerialBusAsync(db, device, cancellationToken);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            foreach (var tag in await db.Tags.AsNoTracking().Where(x => x.DeviceId == id).ToListAsync(cancellationToken))
            {
                error = ConfigurationValidator.Tag(tag, device);
                if (error is not null) return EndpointHelpers.BadRequest($"已有测点“{tag.Name}”：{error}；请先调整测点再切换协议");
            }
            db.OperationLogs.Add(Audit.Change(http, $"修改设备：{device.Name}", "Device", device.Id, before, device));
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(device);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (device is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireDeviceAsync(http, id, Permission.Configure, $"删除设备 #{id}") is { } denied) return denied;
            if (await db.ObjectInstances.AnyAsync(x => x.DeviceId == id, cancellationToken))
                return EndpointHelpers.BadRequest("此采集设备仍有模板对象，请先解除对象关联");
            if (await db.TagDependencies.AnyAsync(x => db.Tags.Any(t => t.Id == x.InputTagId && t.DeviceId == id), cancellationToken))
                return EndpointHelpers.BadRequest("设备测点仍被计算定义引用，请先修改依赖定义");
            db.Devices.Remove(device);
            db.OperationLogs.Add(Audit.Change(http, $"删除设备：{device.Name}", "Device", device.Id, device, null));
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException) { return Results.Conflict(new { message = "设备测点引用已变化，请重新读取后修改依赖定义" }); }
            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/{id:int}/test", (
            int id,
            DeviceProbeRequest? request,
            IDbContextFactory<ConfigDbContext> contextFactory,
            IDeviceDriverFactory driverFactory,
            IReadPlanBuilder planBuilder,
            GatewayCenterService gateways, HttpContext http,
            CancellationToken cancellationToken) =>
            GatewayEndpoints.Run(http, async () =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (device is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireDeviceAsync(http, id, Permission.Control, $"设备连通测试 #{id}") is { } denied) return denied;
            if (device.GatewayId != null) return Results.Ok(await gateways.EngineerAsync<DeviceProbeResult>(id, GatewayEngineeringOperation.Probe, request ?? new DeviceProbeRequest(), http.User.GetUserId()!.Value, cancellationToken));
            try
            {
                return Results.Ok(await DeviceProbe.ReadAsync(device, request, driverFactory, planBuilder, cancellationToken));
            }
            catch (ArgumentException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        })).RequireAuthorization(AccessPolicies.Control);
    }

    private static string? ApplyOpcSettings(Device device, DeviceRequest request, IDeviceCredentials credentials)
    {
        if (request.OpcPassword?.Length > 4096) return "OPC UA 密码过长";
        device.OpcEndpointUrl = request.OpcEndpointUrl?.Trim() ?? ""; device.OpcUserName = request.OpcUserName?.Trim() ?? "";
        device.OpcSecurityMode = request.OpcSecurityMode; device.OpcSecurityPolicy = request.OpcSecurityPolicy;
        if (request.ClearOpcPassword) device.OpcPasswordProtected = null;
        else if (request.OpcPassword is not null) device.OpcPasswordProtected = credentials.Protect(request.OpcPassword);
        return null;
    }

    private static async Task<string?> ValidateSerialBusAsync(ConfigDbContext db, Device device, CancellationToken ct)
    {
        if (!device.Enabled || device.Protocol != ProtocolType.ModbusRtu) return null;
        var others = await db.Devices.AsNoTracking().Where(x => x.Id != device.Id && x.Enabled && x.Protocol == ProtocolType.ModbusRtu && x.GatewayId == device.GatewayId).ToListAsync(ct);
        foreach (var other in others.Where(x => string.Equals(x.SerialPort.Trim(), device.SerialPort.Trim(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            if (other.BaudRate != device.BaudRate || other.DataBits != device.DataBits || other.Parity != device.Parity || other.StopBits != device.StopBits)
                return "同一串口上的启用设备必须使用相同通信参数";
            if (other.StationNumber == device.StationNumber) return "同一串口不能重复配置相同站号的启用设备";
        }
        return null;
    }
}
