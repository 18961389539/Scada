using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Options;
using Scada.Core.Simulation;
using Scada.Data;

namespace Scada.Server.Services;

/// <summary>
/// 设备写入的唯一落地点，与 <see cref="TagCommandService"/> 同形：REST 端点与 MCP 工具
/// 共用同一套校验顺序（协议字段 → 通道站号 → 串口总线 → 全局重名）与落盘顺序
/// （配置库 → 审计 → 重载）。
///
/// 之前这两条路径各自把校验抄了一遍，顺序还不同：端点先查重名再校验协议，MCP 反过来。
/// 通道端点下发（<see cref="ApplyChannelEndpointAsync"/>）必须在校验之前跑——设备的
/// Host/Port/串口参数由通道覆盖后，协议校验才有意义。
/// </summary>
public sealed class DeviceCommandService(
    ConfigReloader reloader,
    IOptions<ScadaSettings> settings,
    IProtocolDriverAvailability drivers,
    ISimulationRuntime simulations)
{
    public const string DuplicateNameMessage = "设备名称已存在";

    public async Task<DeviceCommandResult> CreateAsync(
        ConfigDbContext db, Device device, CancellationToken cancellationToken)
    {
        if (!await db.Areas.AnyAsync(x => x.Id == device.AreaId, cancellationToken))
            return DeviceCommandResult.Failed(DeviceCommandError.Validation, "区域不存在");

        if (await ApplyChannelEndpointAsync(db, device, cancellationToken) is string channelError)
            return DeviceCommandResult.Failed(DeviceCommandError.Validation, channelError);

        if (await InspectAsync(db, device, null, cancellationToken) is string invalid)
            return DeviceCommandResult.Failed(DeviceCommandError.Validation, invalid);

        db.Devices.Add(device);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return DeviceCommandResult.Failed(DeviceCommandError.DuplicateName, DuplicateNameMessage);
        }

        await ReloadAsync(cancellationToken);
        return DeviceCommandResult.Succeeded(device);
    }

    /// <summary>
    /// 调用方负责把请求字段合并到被跟踪的 <paramref name="device"/>（含 <c>DisabledSinceTs</c>），
    /// 本方法只做「通道下发 → 校验 → 已有测点重校验 → 落盘」。
    /// </summary>
    public async Task<DeviceCommandResult> UpdateAsync(
        ConfigDbContext db, Device device, CancellationToken cancellationToken)
    {
        if (await ApplyChannelEndpointAsync(db, device, cancellationToken) is string channelError)
            return DeviceCommandResult.Failed(DeviceCommandError.Validation, channelError);

        if (await InspectAsync(db, device, device.Id, cancellationToken) is string invalid)
            return DeviceCommandResult.Failed(DeviceCommandError.Validation, invalid);

        // 切换协议/地址后，设备上已有的测点必须仍然合法，否则会留下静默采不到的坏点。
        foreach (var tag in await db.Tags.AsNoTracking().Where(x => x.DeviceId == device.Id).ToListAsync(cancellationToken))
        {
            if (ConfigurationValidator.Tag(tag, device) is string tagError)
                return DeviceCommandResult.Failed(DeviceCommandError.Validation, $"已有测点“{tag.Name}”：{tagError}；请先调整测点再切换协议");
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return DeviceCommandResult.Failed(DeviceCommandError.DuplicateName, DuplicateNameMessage);
        }

        await ReloadAsync(cancellationToken);
        return DeviceCommandResult.Succeeded(device);
    }

    public async Task<DeviceCommandResult> DeleteAsync(
        ConfigDbContext db, Device device, CancellationToken cancellationToken)
    {
        if (await db.ObjectInstances.AnyAsync(x => x.DeviceId == device.Id, cancellationToken))
            return DeviceCommandResult.Failed(DeviceCommandError.Referenced, "此采集设备仍有模板对象，请先解除对象关联");

        if (await db.TagDependencies.AnyAsync(
                x => db.Tags.Any(t => t.Id == x.InputTagId && t.DeviceId == device.Id), cancellationToken))
            return DeviceCommandResult.Failed(DeviceCommandError.Referenced, "设备测点仍被计算定义引用，请先修改依赖定义");

        db.Devices.Remove(device);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return DeviceCommandResult.Failed(DeviceCommandError.Conflict, "设备测点引用已变化，请重新读取后修改依赖定义");
        }

        // 仿真运行时按设备号记账，删除后必须同时摘掉，否则下次同号设备会继承旧仿真状态。
        simulations.Remove(device.Id);
        await ReloadAsync(cancellationToken);
        return DeviceCommandResult.Succeeded(device);
    }

    /// <summary>校验顺序固定，两条入口共用。</summary>
    public async Task<string?> InspectAsync(
        ConfigDbContext db, Device device, int? excludeDeviceId, CancellationToken cancellationToken)
    {
        if (ConfigurationValidator.Device(device, settings.Value, drivers) is string invalid) return invalid;
        if (await ValidateChannelStationAsync(db, device, cancellationToken) is string station) return station;
        if (await ValidateSerialBusAsync(db, device, cancellationToken) is string bus) return bus;

        var duplicate = excludeDeviceId is int exclude
            ? await db.Devices.AnyAsync(x => x.Id != exclude && x.Name == device.Name, cancellationToken)
            : await db.Devices.AnyAsync(x => x.Name == device.Name, cancellationToken);
        return duplicate ? DuplicateNameMessage : null;
    }

    /// <summary>
    /// 通道是端点权威：绑定后把通道的端点参数下发到设备，写点、诊断、出站白名单、
    /// 工程导出等既有路径因此不需要理解"通道"。通道改端点时由通道接口统一回写。
    /// </summary>
    public static async Task<string?> ApplyChannelEndpointAsync(ConfigDbContext db, Device device, CancellationToken ct)
    {
        if (device.ChannelId is not int channelId) return null;
        // 先判协议能否共享链路：OpcUa/S7 这类会话说协议绑通道本身就没有意义，
        // 报"协议不一致"会让人以为是选错了通道，而不是选错了做法。
        if (!ChannelProtocols.Supports(device.Protocol))
            return "该协议无法共享物理通道；只有 Modbus TCP / RTU over TCP / RTU / ASCII 支持绑定通道";
        var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == channelId, ct);
        if (channel is null) return "通道不存在";
        if (channel.Protocol != device.Protocol)
            return $"通道「{channel.Name}」使用 {channel.Protocol}，与设备协议 {device.Protocol} 不一致";
        if (ChannelProtocols.UsesSerial(channel.Protocol))
        {
            device.SerialPort = channel.SerialPort;
            device.BaudRate = channel.BaudRate;
            device.DataBits = channel.DataBits;
            device.Parity = channel.Parity;
            device.StopBits = channel.StopBits;
        }
        else
        {
            device.Host = channel.Host;
            device.Port = channel.Port;
        }
        // 一条物理链路只有一个超时：成员设备的超时由通道统一决定。
        device.TimeoutMs = channel.TimeoutMs;
        return null;
    }

    // 串口是独占资源：没有通道时，驱动为每台设备各打开一次 SerialPort，同一 COM 口上的
    // 第二台设备会在 Open() 失败后永久停留在重连退避（现场表现为"其中一台一直离线"）。
    // 绑定同一通道的设备共用一条链路（一次 Open、站号区分），因此只有"不同归属"才算冲突。
    private static async Task<string?> ValidateSerialBusAsync(ConfigDbContext db, Device device, CancellationToken ct)
    {
        if (device.SimulationEnabled || !device.Enabled || device.Protocol is not (ProtocolType.ModbusRtu or ProtocolType.ModbusAscii)) return null;
        var port = device.SerialPort.Trim();
        var others = await db.Devices.AsNoTracking()
            .Where(x => x.Id != device.Id && x.Enabled && x.GatewayId == device.GatewayId
                && (x.Protocol == ProtocolType.ModbusRtu || x.Protocol == ProtocolType.ModbusAscii))
            .ToListAsync(ct);
        var conflict = others.FirstOrDefault(x => string.Equals(x.SerialPort.Trim(), port, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && !SharesChannel(device, x));
        if (conflict is null) return null;
        return device.ChannelId is null
            ? $"串口 {port} 已被启用设备「{conflict.Name}」占用。要让同一 RS-485 总线上的多台从站共享串口，请创建「通道」并把两台设备绑定到同一通道；否则请改用其它串口，或改用 Modbus RTU over TCP。"
            : $"串口 {port} 已被另一条通道上的设备「{conflict.Name}」占用。同一串口只能由一个通道独占，请改用其它串口。";
    }

    // 同一通道上的站号必须唯一：否则两台设备的请求会打到同一个从站，读数互相覆盖。
    private static async Task<string?> ValidateChannelStationAsync(ConfigDbContext db, Device device, CancellationToken ct)
    {
        if (device.ChannelId is not int channelId || !device.Enabled || !ChannelProtocols.Supports(device.Protocol)) return null;
        var others = await db.Devices.AsNoTracking()
            .Where(x => x.Id != device.Id && x.Enabled && x.ChannelId == channelId)
            .Select(x => new { x.Name, x.StationNumber })
            .ToListAsync(ct);
        var clash = others.FirstOrDefault(x => x.StationNumber == device.StationNumber);
        return clash is null ? null : $"站号 {device.StationNumber} 已被同一通道上的设备「{clash.Name}」占用，请改用其它站号";
    }

    private static bool SharesChannel(Device left, Device right) =>
        left.ChannelId is int channelId && channelId == right.ChannelId;

    // 审计由 AuditInterceptor 在 SaveChanges 后自动落，这里只负责让运行时重新加载配置。
    private Task ReloadAsync(CancellationToken cancellationToken) => reloader.ReloadAsync(cancellationToken);
}

public enum DeviceCommandError
{
    None = 0,
    Validation,
    DuplicateName,
    Referenced,
    Conflict
}

public sealed record DeviceCommandResult(bool Success, DeviceCommandError Error, string Message, Device? Device)
{
    public static DeviceCommandResult Succeeded(Device device) => new(true, DeviceCommandError.None, string.Empty, device);
    public static DeviceCommandResult Failed(DeviceCommandError error, string message) => new(false, error, message, null);
}
