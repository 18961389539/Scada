using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;
using Scada.Server.Services;

namespace Scada.Server.Endpoints;

public sealed record ChannelRequest(
    string Name,
    ProtocolType Protocol,
    string? Description = null,
    string? Host = null,
    int Port = 502,
    string? SerialPort = null,
    int BaudRate = 9600,
    int DataBits = 8,
    SerialParity Parity = SerialParity.None,
    SerialStopBits StopBits = SerialStopBits.One,
    int TimeoutMs = 3000,
    bool Enabled = true);

/// <summary>
/// 通道是物理链路（一条 TCP 连接或一个串口），可被多台从站设备共享。
/// 它是端点的唯一权威：保存后会把端点参数回写到成员设备，因此设备的 Host/Port/串口字段始终与通道一致。
/// </summary>
public static class ChannelEndpoints
{
    private static bool SamePort(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static AcquisitionChannel Map(AcquisitionChannel channel, ChannelRequest request)
    {
        channel.Name = request.Name?.Trim() ?? "";
        channel.Description = request.Description?.Trim() ?? "";
        channel.Protocol = request.Protocol;
        channel.Host = request.Host?.Trim() ?? "";
        channel.Port = request.Port;
        channel.SerialPort = request.SerialPort?.Trim() ?? "";
        channel.BaudRate = request.BaudRate;
        channel.DataBits = request.DataBits;
        channel.Parity = request.Parity;
        channel.StopBits = request.StopBits;
        channel.TimeoutMs = request.TimeoutMs;
        channel.Enabled = request.Enabled;
        return channel;
    }

    // 一个串口只能有一个归属：要么属于本通道，要么属于某台独占设备，要么属于另一条通道。
    private static async Task<string?> ValidateSerialOwnershipAsync(ConfigDbContext db, AcquisitionChannel channel, CancellationToken ct)
    {
        if (!ChannelProtocols.UsesSerial(channel.Protocol)) return null;
        var devices = await db.Devices.AsNoTracking()
            .Where(x => x.Enabled && (x.ChannelId == null || x.ChannelId != channel.Id)
                && (x.Protocol == ProtocolType.ModbusRtu || x.Protocol == ProtocolType.ModbusAscii))
            .Select(x => new { x.Name, x.SerialPort, x.ChannelId })
            .ToListAsync(ct);
        var busy = devices.FirstOrDefault(x => SamePort(x.SerialPort, channel.SerialPort));
        if (busy is null) return null;
        return busy.ChannelId is null
            ? $"串口 {channel.SerialPort.Trim()} 已被启用设备「{busy.Name}」独占，请先把该设备绑定到本通道（或其它通道），或改用其它串口"
            : $"串口 {channel.SerialPort.Trim()} 已被另一条通道上的设备「{busy.Name}」占用，请改用其它串口";
    }

    public static void MapChannelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/channels").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("", async (
            IDbContextFactory<ConfigDbContext> factory,
            CancellationToken cancellationToken) =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var channels = await db.Channels.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
            var counts = await db.Devices.AsNoTracking()
                .Where(x => x.ChannelId != null)
                .GroupBy(x => x.ChannelId!.Value)
                .Select(g => new { ChannelId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ChannelId, x => x.Count, cancellationToken);
            return Results.Ok(channels.Select(x => new
            {
                x.Id, x.Name, x.Description, x.Protocol, x.Host, x.Port, x.SerialPort,
                x.BaudRate, x.DataBits, x.Parity, x.StopBits, x.TimeoutMs, x.Enabled,
                deviceCount = counts.GetValueOrDefault(x.Id)
            }));
        }).RequireAuthorization(AccessPolicies.View);

        // 总线级观测：共享链路上的稀缺资源是"链路占用时间"，只看设备在线与否发现不了
        // "某台设备的读取计划把总线吃满、其它设备全在排队"这类问题。
        group.MapGet("/{id:int}/diagnostics", async (
            int id,
            IDbContextFactory<ConfigDbContext> factory,
            ICollectorDiagnostics collector,
            CancellationToken cancellationToken) =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (channel is null) return Results.NotFound();
            var endpoint = ChannelProtocols.UsesSerial(channel.Protocol)
                ? $"{channel.SerialPort.Trim()} {channel.BaudRate},{channel.DataBits},{channel.Parity},{channel.StopBits}"
                : $"{channel.Host.Trim()}:{channel.Port}";
            // 采集侧不加载通道主数据，名称与协议由这里补齐。
            return Results.Ok(collector.GetChannelStatistics(id) with
            {
                Name = channel.Name,
                Protocol = channel.Protocol.ToString(),
                Endpoint = endpoint
            });
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("", async (
            ChannelRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            var channel = Map(new AcquisitionChannel(), request);
            var error = ConfigurationValidator.Channel(channel, DeviceEndpoints.Settings(http));
            if (error is not null) return EndpointHelpers.BadRequest(error);

            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (await db.Channels.AnyAsync(x => x.Name == channel.Name, cancellationToken)) return EndpointHelpers.BadRequest("通道名称已存在");
            error = await ValidateSerialOwnershipAsync(db, channel, cancellationToken);
            if (error is not null) return EndpointHelpers.BadRequest(error);

            db.Channels.Add(channel);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                db.OperationLogs.Add(Audit.Change(http, $"创建通道：{channel.Name}", "Channel", channel.Id, null, channel));
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return EndpointHelpers.BadRequest("通道名称已存在");
            }
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(channel);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id,
            ChannelRequest request,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var channel = await db.Channels.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (channel is null) return Results.NotFound();

            var before = channel.Clone();
            Map(channel, request);
            var error = ConfigurationValidator.Channel(channel, DeviceEndpoints.Settings(http));
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.Channels.AnyAsync(x => x.Id != id && x.Name == channel.Name, cancellationToken)) return EndpointHelpers.BadRequest("通道名称已存在");

            var members = await db.Devices.Where(x => x.ChannelId == id).ToListAsync(cancellationToken);
            // 通道协议决定帧格式与端点类型，成员设备必须一致；否则先解绑再改。
            var mismatched = members.FirstOrDefault(x => x.Protocol != channel.Protocol);
            if (mismatched is not null)
                return EndpointHelpers.BadRequest($"通道上已有使用 {mismatched.Protocol} 的设备「{mismatched.Name}」，请先解除绑定或保持协议一致");
            error = await ValidateSerialOwnershipAsync(db, channel, cancellationToken);
            if (error is not null) return EndpointHelpers.BadRequest(error);

            // 端点下发：通道是权威，成员设备的连接参数必须跟着走，否则旧连接会打到旧地址。
            foreach (var member in members)
            {
                if (ChannelProtocols.UsesSerial(channel.Protocol))
                {
                    member.SerialPort = channel.SerialPort;
                    member.BaudRate = channel.BaudRate;
                    member.DataBits = channel.DataBits;
                    member.Parity = channel.Parity;
                    member.StopBits = channel.StopBits;
                }
                else
                {
                    member.Host = channel.Host;
                    member.Port = channel.Port;
                }
                member.TimeoutMs = channel.TimeoutMs;
            }

            db.OperationLogs.Add(Audit.Change(http, $"修改通道：{channel.Name}", "Channel", channel.Id, before, channel));
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.Ok(channel);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> factory,
            ConfigReloader reloader,
            CancellationToken cancellationToken) =>
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var channel = await db.Channels.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (channel is null) return Results.NotFound();
            // 不静默解绑：解绑会让设备退回"用自己那份端点字段直连"，端点可能早已过期。
            var bound = await db.Devices.AsNoTracking().Where(x => x.ChannelId == id).Select(x => x.Name).Take(5).ToListAsync(cancellationToken);
            if (bound.Count > 0)
            {
                var names = string.Join("、", bound);
                return EndpointHelpers.BadRequest($"通道仍被 {names} 等设备使用，请先解除绑定");
            }

            db.Channels.Remove(channel);
            db.OperationLogs.Add(Audit.Change(http, $"删除通道：{channel.Name}", "Channel", channel.Id, channel, null));
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);
    }
}
