using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Services;

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
    OpcSecurityPolicy OpcSecurityPolicy = OpcSecurityPolicy.Basic256Sha256, string OpcUserName = "", string? OpcPassword = null, bool ClearOpcPassword = false);

public sealed record TestConnectionRequest(TagArea? Area, int? Address, TagDataType? DataType, int DbNumber = 1, int BitOffset = 0, string NodeId = "");

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices");

        group.MapGet("", async (
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var devices = await db.Devices.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            return Results.Ok(devices);
        });

        group.MapGet("/{id:int}", async (
            int id,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return device is null ? Results.NotFound() : Results.Ok(device);
        });

        group.MapPost("", async (
            DeviceRequest request,
            IDeviceCredentials credentials,
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var device = new Device
            {
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
            }
            catch (DbUpdateException)
            {
                return EndpointHelpers.BadRequest("设备名称已存在");
            }

            await reloader.ReloadAsync(cancellationToken);
            return Results.Created($"/api/devices/{device.Id}", device);
        }).RequireAuthorization("Admin");

        group.MapPut("/{id:int}", async (
            int id,
            DeviceRequest request,
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
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(device);
        }).RequireAuthorization("Admin");

        group.MapDelete("/{id:int}", async (
            int id,
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

            if (await db.ObjectInstances.AnyAsync(x => x.DeviceId == id, cancellationToken))
                return EndpointHelpers.BadRequest("此采集设备仍有模板对象，请先解除对象关联");
            db.Devices.Remove(device);
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization("Admin");

        group.MapPost("/{id:int}/test", async (
            int id,
            TestConnectionRequest? request,
            IDbContextFactory<ConfigDbContext> contextFactory,
            IDeviceDriverFactory driverFactory,
            IReadPlanBuilder planBuilder,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (device is null)
            {
                return Results.NotFound();
            }

            if (device.GatewayId != null) return Results.Conflict(new { message = "远程读取测试通道尚未贯通，未在中心主机发起连接" });
            var probe = new Tag
            {
                Id = -1,
                DeviceId = device.Id,
                Name = "连接测试",
                Area = request?.Area ?? (device.Protocol == ProtocolType.OpcUa ? TagArea.OpcNode : device.Protocol == ProtocolType.SiemensS7 ? TagArea.DataBlock : TagArea.HoldingRegister),
                Address = request?.Address ?? 0,
                DataType = request?.DataType ?? TagDataType.Float,
                Scale = 1,
                DbNumber = request?.DbNumber ?? 1, BitOffset = request?.BitOffset ?? 0, NodeId = request?.NodeId ?? ""
            };
            var error = ConfigurationValidator.Tag(probe, device);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            var plan = planBuilder.Build(device, new[] { probe });
            await using var driver = driverFactory.Create(device);
            try
            {
                await driver.ConnectAsync(device, cancellationToken);
                var values = await driver.ReadAsync(device, plan, cancellationToken);
                var value = values.FirstOrDefault();
                if (value?.Quality != TagQuality.Good) return Results.Ok(new { connected = true, quality = value?.Quality, message = value?.Error ?? "读取没有返回可用值" });
                var raw = values.FirstOrDefault()?.Value ?? 0;
                return Results.Ok(new
                {
                    connected = true,
                    area = probe.Area.ToString(),
                    address = probe.Address,
                    dataType = probe.DataType.ToString(),
                    value = raw
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Ok(new
                {
                    connected = false,
                    message = ex.Message
                });
            }
        }).RequireAuthorization("Operator");
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
