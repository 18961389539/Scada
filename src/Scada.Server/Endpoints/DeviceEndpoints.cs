using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
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
    bool Enabled);

public sealed record TestConnectionRequest(TagArea? Area, int? Address, TagDataType? DataType);

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
            IDbContextFactory<ConfigDbContext> contextFactory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var device = new Device
            {
                Name = request.Name.Trim(),
                Protocol = request.Protocol,
                Host = request.Host.Trim(),
                Port = request.Port,
                StationNumber = request.StationNumber,
                ByteOrder = request.ByteOrder,
                PollIntervalMs = request.PollIntervalMs,
                TimeoutMs = request.TimeoutMs,
                Enabled = request.Enabled
            };
            var error = EndpointHelpers.ValidateDevice(device);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
            }

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
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

            var name = request.Name.Trim();
            var host = request.Host.Trim();
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
            device.Enabled = request.Enabled;

            var error = EndpointHelpers.ValidateDevice(device);
            if (error is not null)
            {
                return EndpointHelpers.BadRequest(error);
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

            var probe = new Tag
            {
                Id = -1,
                DeviceId = device.Id,
                Name = "连接测试",
                Area = request?.Area ?? TagArea.HoldingRegister,
                Address = request?.Address ?? 0,
                DataType = request?.DataType ?? TagDataType.Float,
                Scale = 1
            };
            var error = EndpointHelpers.ValidateTag(probe);
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
}
