using System.ComponentModel;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Data;
using Scada.Server.Endpoints;
using Scada.Server.Services;
using static Scada.Server.Mcp.McpActors;
using static Scada.Server.Mcp.McpText;

namespace Scada.Server.Mcp;

// 主数据维护：区域、设备、测点、报警规则。与 REST 端点共用同一套校验（ConfigurationValidator /
// EndpointHelpers.ValidateAlarmRule）和 ConfigReloader 热加载，审计身份固定 mcp:lan。
// 修改类工具只覆盖显式传入的字段：不传即保持不变，避免 LLM 因为漏字段把配置改回默认值。
[McpServerToolType]
public sealed class McpMasterDataTools(
    AccessModelProvider accessModels,
    IDbContextFactory<ConfigDbContext> factory,
    ConfigReloader reloader,
    IDeviceCredentials deviceCredentials,
    McpIdempotency idempotency)
{
    // ---------- 区域 ----------

    [McpServerTool(Name = "create_area", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create one configuration area. Name must be 1-100 characters and unique; description is optional (max 500). Areas scope devices and area permissions. Returns success=false when the name is invalid or already used.")]
    public Task<McpAreaSaved> CreateArea(string name, CancellationToken cancellationToken,
        [Description("Optional description, at most 500 characters.")] string? description = null,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("create_area", requestId, async () =>
        {
            var clean = RequiredText(name?.Trim(), 100, "name");
            var note = OptionalText(string.IsNullOrWhiteSpace(description) ? null : description.Trim(), 500, "description");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (await db.Areas.AnyAsync(x => x.Name == clean, cancellationToken))
                return new McpAreaSaved(0, false, "区域名称已存在。", clean, note, ScadaTime.NowMs());
            var area = new Area { Name = clean, Description = note };
            db.Areas.Add(area);
            await db.SaveChangesAsync(cancellationToken);
            db.OperationLogs.Add(MasterAudit($"创建区域：{area.Name}", "Area", area.Id, null, area));
            await db.SaveChangesAsync(cancellationToken);
            accessModels.Invalidate();
            return new McpAreaSaved(area.Id, true, "Created.", area.Name, area.Description, ScadaTime.NowMs());
        });

    [McpServerTool(Name = "update_area", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Rename or re-describe one configuration area. Omitted fields keep their current values; pass an empty description to clear it. Returns success=false when the name is invalid or already used.")]
    public async Task<McpAreaSaved> UpdateArea(int areaId, CancellationToken cancellationToken,
        [Description("New name, 1-100 characters. Omit to keep the current name.")] string? name = null,
        [Description("New description, at most 500 characters. Empty string clears it; omit to keep the current value.")] string? description = null)
    {
        if (areaId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "areaId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var area = await db.Areas.FirstOrDefaultAsync(x => x.Id == areaId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Area does not exist.");
        var clean = name is null ? area.Name : RequiredText(name.Trim(), 100, "name");
        var note = description is null ? area.Description : OptionalText(description.Trim(), 500, "description");
        if (await db.Areas.AnyAsync(x => x.Id != areaId && x.Name == clean, cancellationToken))
            return new(areaId, false, "区域名称已存在。", clean, note, ScadaTime.NowMs());
        var before = new Area { Id = area.Id, Name = area.Name, Description = area.Description };
        area.Name = clean;
        area.Description = note;
        db.OperationLogs.Add(MasterAudit($"修改区域：{area.Name}", "Area", area.Id, before, area));
        await db.SaveChangesAsync(cancellationToken);
        accessModels.Invalidate();
        return new(area.Id, true, "Updated.", area.Name, area.Description, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "delete_area", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete one configuration area. Refused with success=false when the area is the default area, still holds devices, is referenced by calculated tags or by user role assignments. Does not delete devices or tags.")]
    public async Task<McpMasterDeleted> DeleteArea(int areaId, CancellationToken cancellationToken)
    {
        if (areaId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "areaId must be positive.");
        if (areaId == Area.DefaultId) return new(areaId, false, "默认区域不能删除。", ScadaTime.NowMs());
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var area = await db.Areas.FirstOrDefaultAsync(x => x.Id == areaId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Area does not exist.");
        if (await db.Devices.AnyAsync(x => x.AreaId == areaId, cancellationToken))
            return new(areaId, false, "区域内仍有设备，请先移走设备。", ScadaTime.NowMs());
        if (await db.Tags.AnyAsync(x => x.AreaId == areaId, cancellationToken))
            return new(areaId, false, "仍有计算测点指定了该区域。", ScadaTime.NowMs());
        if (await db.UserRoleAssignments.AnyAsync(x => x.AreaId == areaId, cancellationToken))
            return new(areaId, false, "仍有用户授权限定在该区域，请先调整授权。", ScadaTime.NowMs());
        db.Areas.Remove(area);
        db.OperationLogs.Add(MasterAudit($"删除区域：{area.Name}", "Area", area.Id, area, null));
        await db.SaveChangesAsync(cancellationToken);
        accessModels.Invalidate();
        return new(areaId, true, "Deleted.", ScadaTime.NowMs());
    }

    // ---------- 设备 ----------

    [McpServerTool(Name = "create_device", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create one acquisition device. Validation and serial-bus rules match the configuration UI: host/port for TCP protocols, serial settings for RTU/ASCII, CPU/rack/slot for S7, endpoint and security for OPC UA. OPC passwords are stored protected and never returned. Returns success=false on validation failures or duplicate names.")]
    public Task<McpDeviceSaved> CreateDevice(
        string name, ProtocolType protocol, CancellationToken cancellationToken,
        [Description("Device host or IP. Required by TCP-based protocols.")] string host = "",
        [Description("TCP port, 1-65535 for TCP protocols.")] int port = 0,
        [Description("Modbus station number, 1-247 for RTU/ASCII.")] int stationNumber = 1,
        ByteOrder byteOrder = ByteOrder.ABCD,
        int pollIntervalMs = 1000, int timeoutMs = 1000, bool enabled = true,
        [Description("Area to place the device in. Omit for the default area.")] int? areaId = null,
        string serialPort = "COM1", int baudRate = 9600, int dataBits = 8,
        SerialParity parity = SerialParity.None, SerialStopBits stopBits = SerialStopBits.One,
        S7Cpu cpu = S7Cpu.S71200, short rack = 0, short slot = 1,
        string opcEndpointUrl = "opc.tcp://localhost:4840",
        OpcSecurityMode opcSecurityMode = OpcSecurityMode.SignAndEncrypt,
        OpcSecurityPolicy opcSecurityPolicy = OpcSecurityPolicy.Basic256Sha256,
        string opcUserName = "", [Description("OPC UA password; stored protected.")] string? opcPassword = null,
        [Description("Protocol-specific options JSON, only for protocols that use it.")] string? protocolOptions = null,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("create_device", requestId, async () =>
        {
            if (stationNumber is < 0 or > 255)
                throw new McpToolException(McpErrorCodes.InvalidArgument, "stationNumber must be between 0 and 255.");
            var targetArea = areaId ?? Area.DefaultId;
            var request = new DeviceRequest(name?.Trim() ?? "", protocol, host?.Trim() ?? "", port, (byte)stationNumber,
                byteOrder, pollIntervalMs, timeoutMs, enabled, serialPort?.Trim() ?? "", baudRate, dataBits, parity, stopBits,
                cpu, rack, slot, opcEndpointUrl?.Trim() ?? "", opcSecurityMode, opcSecurityPolicy, opcUserName?.Trim() ?? "",
                opcPassword, false, targetArea, protocolOptions?.Trim());
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (!await db.Areas.AnyAsync(x => x.Id == targetArea, cancellationToken))
                return new McpDeviceSaved(0, false, "区域不存在。", request.Name, targetArea, enabled, ScadaTime.NowMs());
            var device = new Device
            {
                AreaId = targetArea, Name = request.Name, Protocol = protocol, Host = request.Host, Port = port,
                StationNumber = (byte)stationNumber, ByteOrder = byteOrder, PollIntervalMs = pollIntervalMs,
                TimeoutMs = timeoutMs, Enabled = enabled, DisabledSinceTs = enabled ? 0 : ScadaTime.NowMs(),
                SerialPort = request.SerialPort, BaudRate = baudRate, DataBits = dataBits, Parity = parity,
                StopBits = stopBits, Cpu = cpu, Rack = rack, Slot = slot
            };
            var credentialError = DeviceEndpoints.ApplyOpcSettings(device, request, deviceCredentials);
            if (credentialError is not null) return new McpDeviceSaved(0, false, credentialError, device.Name, targetArea, enabled, ScadaTime.NowMs());
            var error = EndpointHelpers.ValidateDevice(device);
            if (error is not null) return new McpDeviceSaved(0, false, error, device.Name, targetArea, enabled, ScadaTime.NowMs());
            error = await DeviceEndpoints.ValidateSerialBusAsync(db, device, cancellationToken);
            if (error is not null) return new McpDeviceSaved(0, false, error, device.Name, targetArea, enabled, ScadaTime.NowMs());
            if (await db.Devices.AnyAsync(x => x.Name == device.Name, cancellationToken))
                return new McpDeviceSaved(0, false, "设备名称已存在。", device.Name, targetArea, enabled, ScadaTime.NowMs());
            db.Devices.Add(device);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                db.OperationLogs.Add(MasterAudit($"创建设备：{device.Name}", "Device", device.Id, null, device));
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new McpDeviceSaved(0, false, "设备名称已存在。", device.Name, targetArea, enabled, ScadaTime.NowMs());
            }
            await reloader.ReloadAsync(cancellationToken);
            return new McpDeviceSaved(device.Id, true, "Created.", device.Name, device.AreaId, device.Enabled, ScadaTime.NowMs());
        });

    [McpServerTool(Name = "update_device", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Change one acquisition device. Only supplied fields change; omitted fields keep their current values. Re-validates the device, its serial bus and every tag already bound to it, so switching protocol cannot silently break existing tags. Returns success=false when validation fails.")]
    public async Task<McpDeviceSaved> UpdateDevice(int deviceId, CancellationToken cancellationToken,
        string? name = null, ProtocolType? protocol = null, string? host = null, int? port = null,
        int? stationNumber = null, ByteOrder? byteOrder = null, int? pollIntervalMs = null, int? timeoutMs = null,
        bool? enabled = null, int? areaId = null, string? serialPort = null, int? baudRate = null,
        int? dataBits = null, SerialParity? parity = null, SerialStopBits? stopBits = null, S7Cpu? cpu = null,
        short? rack = null, short? slot = null, string? opcEndpointUrl = null,
        OpcSecurityMode? opcSecurityMode = null, OpcSecurityPolicy? opcSecurityPolicy = null,
        string? opcUserName = null, string? opcPassword = null, bool clearOpcPassword = false,
        string? protocolOptions = null)
    {
        if (deviceId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "deviceId must be positive.");
        if (stationNumber is < 0 or > 255)
            throw new McpToolException(McpErrorCodes.InvalidArgument, "stationNumber must be between 0 and 255.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
        var targetArea = areaId ?? device.AreaId;
        if (targetArea != device.AreaId && !await db.Areas.AnyAsync(x => x.Id == targetArea, cancellationToken))
            return new(deviceId, false, "区域不存在。", device.Name, device.AreaId, device.Enabled, ScadaTime.NowMs());
        var request = new DeviceRequest(
            (name ?? device.Name).Trim(), protocol ?? device.Protocol, (host ?? device.Host).Trim(), port ?? device.Port,
            (byte)(stationNumber ?? device.StationNumber), byteOrder ?? device.ByteOrder,
            pollIntervalMs ?? device.PollIntervalMs, timeoutMs ?? device.TimeoutMs, enabled ?? device.Enabled,
            (serialPort ?? device.SerialPort).Trim(), baudRate ?? device.BaudRate, dataBits ?? device.DataBits,
            parity ?? device.Parity, stopBits ?? device.StopBits, cpu ?? device.Cpu, rack ?? device.Rack, slot ?? device.Slot,
            (opcEndpointUrl ?? device.OpcEndpointUrl).Trim(), opcSecurityMode ?? device.OpcSecurityMode,
            opcSecurityPolicy ?? device.OpcSecurityPolicy, (opcUserName ?? device.OpcUserName).Trim(),
            opcPassword, clearOpcPassword, targetArea, (protocolOptions ?? device.ProtocolOptions).Trim());
        var before = device.Clone();
        var updated = new Device { Id = device.Id, AreaId = targetArea, DisabledSinceTs = device.DisabledSinceTs };
        var credentialError = DeviceEndpoints.ApplyOpcSettings(updated, request, deviceCredentials);
        if (credentialError is not null) return new(deviceId, false, credentialError, device.Name, targetArea, device.Enabled, ScadaTime.NowMs());
        var error = EndpointHelpers.ValidateDevice(updated);
        if (error is not null) return new(deviceId, false, error, device.Name, targetArea, device.Enabled, ScadaTime.NowMs());
        error = await DeviceEndpoints.ValidateSerialBusAsync(db, updated, cancellationToken);
        if (error is not null) return new(deviceId, false, error, device.Name, targetArea, device.Enabled, ScadaTime.NowMs());
        foreach (var tag in await db.Tags.AsNoTracking().Where(x => x.DeviceId == deviceId).ToListAsync(cancellationToken))
        {
            error = ConfigurationValidator.Tag(tag, updated);
            if (error is not null)
                return new(deviceId, false, $"已有测点“{tag.Name}”：{error}；请先调整测点再切换协议。", device.Name, targetArea, device.Enabled, ScadaTime.NowMs());
        }
        if (await db.Devices.AnyAsync(x => x.Id != deviceId && x.Name == updated.Name, cancellationToken))
            return new(deviceId, false, "设备名称已存在。", updated.Name, targetArea, device.Enabled, ScadaTime.NowMs());
        if (device.Enabled && !updated.Enabled) device.DisabledSinceTs = ScadaTime.NowMs();
        device.AreaId = updated.AreaId;
        device.Name = updated.Name;
        device.Protocol = updated.Protocol;
        device.Host = updated.Host;
        device.Port = updated.Port;
        device.StationNumber = updated.StationNumber;
        device.ByteOrder = updated.ByteOrder;
        device.PollIntervalMs = updated.PollIntervalMs;
        device.TimeoutMs = updated.TimeoutMs;
        device.Enabled = updated.Enabled;
        device.SerialPort = updated.SerialPort;
        device.BaudRate = updated.BaudRate;
        device.DataBits = updated.DataBits;
        device.Parity = updated.Parity;
        device.StopBits = updated.StopBits;
        device.Cpu = updated.Cpu;
        device.Rack = updated.Rack;
        device.Slot = updated.Slot;
        device.OpcEndpointUrl = updated.OpcEndpointUrl;
        device.OpcUserName = updated.OpcUserName;
        device.OpcPasswordProtected = updated.OpcPasswordProtected;
        device.OpcSecurityMode = updated.OpcSecurityMode;
        device.OpcSecurityPolicy = updated.OpcSecurityPolicy;
        device.ProtocolOptions = updated.ProtocolOptions;
        db.OperationLogs.Add(MasterAudit($"修改设备：{device.Name}", "Device", device.Id, before, device));
        await db.SaveChangesAsync(cancellationToken);
        await reloader.ReloadAsync(cancellationToken);
        return new(device.Id, true, "Updated.", device.Name, device.AreaId, device.Enabled, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "delete_device", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete one acquisition device and its tags. Refused with success=false when template objects are still bound to it or its tags are still referenced by calculation definitions. Does not touch history data.")]
    public async Task<McpMasterDeleted> DeleteDevice(int deviceId, CancellationToken cancellationToken)
    {
        if (deviceId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "deviceId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == deviceId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
        if (await db.ObjectInstances.AnyAsync(x => x.DeviceId == deviceId, cancellationToken))
            return new(deviceId, false, "此采集设备仍有模板对象，请先解除对象关联。", ScadaTime.NowMs());
        if (await db.TagDependencies.AnyAsync(x => db.Tags.Any(t => t.Id == x.InputTagId && t.DeviceId == deviceId), cancellationToken))
            return new(deviceId, false, "设备测点仍被计算定义引用，请先修改依赖定义。", ScadaTime.NowMs());
        db.Devices.Remove(device);
        db.OperationLogs.Add(MasterAudit($"删除设备：{device.Name}", "Device", device.Id, device, null));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new(deviceId, false, "设备测点引用已变化，请重新读取后再删除。", ScadaTime.NowMs());
        }
        await reloader.ReloadAsync(cancellationToken);
        return new(deviceId, true, "Deleted.", ScadaTime.NowMs());
    }

    // ---------- 测点 ----------

    [McpServerTool(Name = "create_tag", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create one physical tag on an existing device. Area/address rules follow the device protocol (Coil/HoldingRegister/DataBlock/OpcNode etc.); scale 1 and offset 0 archive raw values. Only physical tags can be created here - calculated tags belong to calculation jobs. Returns success=false on validation failures or duplicate names within the device.")]
    public Task<McpTagSaved> CreateTag(int deviceId, string name, TagArea area, TagDataType dataType,
        CancellationToken cancellationToken,
        [Description("Register address, DB offset or OPC node id depending on area.")] int address = 0,
        [Description("OPC node id for OPC UA devices; empty for register-based areas.")] string nodeId = "",
        int dbNumber = 1, int bitOffset = 0, double scale = 1, double offset = 0,
        [Description("Engineering unit, at most 32 characters.")] string? unit = null,
        [Description("Description, at most 500 characters.")] string? description = null,
        bool writable = false, ArchiveMode archiveMode = ArchiveMode.None,
        double deadband = 0, int archivePeriodSec = 0, double? minValue = null, double? maxValue = null,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("create_tag", requestId, async () =>
        {
            if (deviceId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "deviceId must be positive.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, cancellationToken)
                ?? throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
            var tag = new Tag
            {
                DeviceId = deviceId, Name = name?.Trim() ?? "", Area = area, Address = address,
                DbNumber = dbNumber, BitOffset = bitOffset, NodeId = nodeId?.Trim() ?? "", DataType = dataType,
                Scale = scale, Offset = offset, Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(),
                Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Writable = writable, ArchiveMode = archiveMode, Deadband = deadband,
                ArchivePeriodSec = archivePeriodSec, MinValue = minValue, MaxValue = maxValue
            };
            var error = ConfigurationValidator.Tag(tag, device);
            if (error is not null) return new McpTagSaved(0, false, error, tag.Name, deviceId, ScadaTime.NowMs());
            if (await db.Tags.AnyAsync(x => x.DeviceId == deviceId && x.Name == tag.Name, cancellationToken))
                return new McpTagSaved(0, false, "同一设备下测点名称已存在。", tag.Name, deviceId, ScadaTime.NowMs());
            db.Tags.Add(tag);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                db.OperationLogs.Add(MasterAudit($"创建测点：{tag.Name}", "Tag", tag.Id, null, tag));
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                return new McpTagSaved(0, false, "同一设备下测点名称已存在。", tag.Name, deviceId, ScadaTime.NowMs());
            }
            await reloader.ReloadAsync(cancellationToken);
            return new McpTagSaved(tag.Id, true, "Created.", tag.Name, deviceId, ScadaTime.NowMs());
        });

    [McpServerTool(Name = "update_tag", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Change one physical tag. Only supplied fields change; omitted fields keep their current values (pass an empty unit/description to clear it). Re-validates against the target device, so moving a tag to another device or changing area/address is checked. Calculated tags are refused - use calculation jobs instead. Returns success=false on validation failures.")]
    public async Task<McpTagSaved> UpdateTag(int tagId, CancellationToken cancellationToken,
        string? name = null, int? deviceId = null, TagArea? area = null, TagDataType? dataType = null,
        int? address = null, string? nodeId = null, int? dbNumber = null, int? bitOffset = null,
        double? scale = null, double? offset = null, string? unit = null, string? description = null,
        bool? writable = null, ArchiveMode? archiveMode = null, double? deadband = null,
        int? archivePeriodSec = null, double? minValue = null, double? maxValue = null)
    {
        if (tagId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "tagId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tag = await db.Tags.FirstOrDefaultAsync(x => x.Id == tagId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
        if (tag.Kind != TagKind.Physical)
            throw new McpToolException(McpErrorCodes.Unsupported, "计算测点请通过计算任务维护，MCP 只维护物理测点。");
        var targetDeviceId = deviceId ?? tag.DeviceId ?? 0;
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == targetDeviceId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Device does not exist.");
        var before = tag.Clone();
        var updated = new Tag
        {
            Id = tag.Id, Kind = tag.Kind, DeviceId = targetDeviceId,
            Name = (name ?? tag.Name).Trim(), Description = description is null ? tag.Description : OptionalText(description.Trim(), 500, "description"),
            Area = area ?? tag.Area, Address = address ?? tag.Address, DbNumber = dbNumber ?? tag.DbNumber,
            BitOffset = bitOffset ?? tag.BitOffset, NodeId = (nodeId ?? tag.NodeId).Trim(), DataType = dataType ?? tag.DataType,
            Scale = scale ?? tag.Scale, Offset = offset ?? tag.Offset,
            Unit = unit is null ? tag.Unit : OptionalText(unit.Trim(), 32, "unit"),
            Writable = writable ?? tag.Writable, ArchiveMode = archiveMode ?? tag.ArchiveMode,
            Deadband = deadband ?? tag.Deadband, ArchivePeriodSec = archivePeriodSec ?? tag.ArchivePeriodSec,
            MinValue = minValue ?? tag.MinValue, MaxValue = maxValue ?? tag.MaxValue
        };
        var error = ConfigurationValidator.Tag(updated, device);
        if (error is not null) return new(tagId, false, error, updated.Name, targetDeviceId, ScadaTime.NowMs());
        if (await db.Tags.AnyAsync(x => x.Id != tagId && x.DeviceId == targetDeviceId && x.Name == updated.Name, cancellationToken))
            return new(tagId, false, "同一设备下测点名称已存在。", updated.Name, targetDeviceId, ScadaTime.NowMs());
        tag.DeviceId = updated.DeviceId;
        tag.Name = updated.Name;
        tag.Description = updated.Description;
        tag.Area = updated.Area;
        tag.Address = updated.Address;
        tag.DbNumber = updated.DbNumber;
        tag.BitOffset = updated.BitOffset;
        tag.NodeId = updated.NodeId;
        tag.DataType = updated.DataType;
        tag.Scale = updated.Scale;
        tag.Offset = updated.Offset;
        tag.Unit = updated.Unit;
        tag.Writable = updated.Writable;
        tag.ArchiveMode = updated.ArchiveMode;
        tag.Deadband = updated.Deadband;
        tag.ArchivePeriodSec = updated.ArchivePeriodSec;
        tag.MinValue = updated.MinValue;
        tag.MaxValue = updated.MaxValue;
        db.OperationLogs.Add(MasterAudit($"修改测点：{tag.Name}", "Tag", tag.Id, before, tag));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new(tagId, false, "同一设备下测点名称已存在。", updated.Name, targetDeviceId, ScadaTime.NowMs());
        }
        await reloader.ReloadAsync(cancellationToken);
        return new(tag.Id, true, "Updated.", tag.Name, tag.DeviceId ?? 0, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "delete_tag", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete one physical tag. Refused with success=false when the tag is still an input of a calculation definition, or when the tag is calculated (use calculation jobs instead). Archived history rows are not deleted.")]
    public async Task<McpMasterDeleted> DeleteTag(int tagId, CancellationToken cancellationToken)
    {
        if (tagId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "tagId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var tag = await db.Tags.FirstOrDefaultAsync(x => x.Id == tagId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
        if (tag.Kind != TagKind.Physical)
            throw new McpToolException(McpErrorCodes.Unsupported, "计算测点请通过计算任务维护，MCP 只维护物理测点。");
        if (await db.TagDependencies.AnyAsync(x => x.InputTagId == tagId, cancellationToken))
            return new(tagId, false, "测点仍被计算定义引用，请先修改依赖定义。", ScadaTime.NowMs());
        db.Tags.Remove(tag);
        db.OperationLogs.Add(MasterAudit($"删除测点：{tag.Name}", "Tag", tag.Id, tag, null));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return new(tagId, false, "测点引用已变化，请重新读取后再删除。", ScadaTime.NowMs());
        }
        await reloader.ReloadAsync(cancellationToken);
        return new(tagId, true, "Deleted.", ScadaTime.NowMs());
    }

    // ---------- 报警规则 ----------

    [McpServerTool(Name = "create_alarm_rule", ReadOnly = false, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Create one alarm rule on an existing tag. Threshold is ignored for StateChange rules; DelaySec/OffDelaySec and RecoverDeadband suppress chatter. Suppression by condition requires suppressMode other than None plus an existing suppressTagId. Up to one enabled rule per tag and type is expected by the alarm engine. Returns success=false on validation failures.")]
    public Task<McpAlarmRuleSaved> CreateAlarmRule(int tagId, AlarmRuleType type, AlarmLevel level,
        CancellationToken cancellationToken, double threshold = 0, int delaySec = 0, double recoverDeadband = 0,
        bool enabled = true, [Description("Operator-facing message, at most 500 characters. Supports {device} {tag} {value} {unit} {threshold} {recover} placeholders; leave empty to auto-generate a message with current value, unit and threshold.")] string? message = null,
        int offDelaySec = 0, int? suppressTagId = null, AlarmSuppressMode suppressMode = AlarmSuppressMode.None,
        [Description("Why the alarm occurs (root cause), at most 200 characters.")] string? cause = null,
        [Description("What the operator should do, at most 200 characters.")] string? action = null,
        [Description(McpIdempotency.Hint)] Guid? requestId = null) =>
        idempotency.RunAsync("create_alarm_rule", requestId, async () =>
        {
            if (tagId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "tagId must be positive.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            if (!await db.Tags.AnyAsync(x => x.Id == tagId, cancellationToken))
                throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
            if (suppressMode != AlarmSuppressMode.None && suppressTagId is not int conditionId)
                throw new McpToolException(McpErrorCodes.InvalidArgument, "suppressTagId is required when suppressMode is not None.");
            var rule = new AlarmRule
            {
                TagId = tagId, Type = type, Threshold = threshold, Level = level, DelaySec = delaySec,
                RecoverDeadband = recoverDeadband, Enabled = enabled,
                Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
                Cause = string.IsNullOrWhiteSpace(cause) ? null : cause.Trim(),
                Action = string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
                OffDelaySec = offDelaySec,
                SuppressTagId = suppressMode == AlarmSuppressMode.None ? null : suppressTagId,
                SuppressMode = suppressMode
            };
            var error = EndpointHelpers.ValidateAlarmRule(rule);
            if (error is not null) return new McpAlarmRuleSaved(0, false, error, tagId, ScadaTime.NowMs());
            if (rule.SuppressTagId is int suppress && !await db.Tags.AnyAsync(x => x.Id == suppress, cancellationToken))
                return new McpAlarmRuleSaved(0, false, $"条件测点不存在：{suppress}", tagId, ScadaTime.NowMs());
            db.AlarmRules.Add(rule);
            await db.SaveChangesAsync(cancellationToken);
            db.OperationLogs.Add(MasterAudit($"创建报警规则：测点 #{rule.TagId}", "AlarmRule", rule.Id, null, rule));
            await db.SaveChangesAsync(cancellationToken);
            await reloader.ReloadAsync(cancellationToken);
            return new McpAlarmRuleSaved(rule.Id, true, "Created.", rule.TagId, ScadaTime.NowMs());
        });

    [McpServerTool(Name = "update_alarm_rule", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Change one alarm rule. Only supplied fields change; omitted fields keep their current values. Switching suppressMode to None clears the suppress tag. Re-validates thresholds, delays, deadband and suppress condition. Returns success=false on validation failures.")]
    public async Task<McpAlarmRuleSaved> UpdateAlarmRule(int ruleId, CancellationToken cancellationToken,
        int? tagId = null, AlarmRuleType? type = null, double? threshold = null, AlarmLevel? level = null,
        int? delaySec = null, double? recoverDeadband = null, bool? enabled = null, string? message = null,
        int? offDelaySec = null, int? suppressTagId = null, AlarmSuppressMode? suppressMode = null,
        string? cause = null, string? action = null)
    {
        if (ruleId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "ruleId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == ruleId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Alarm rule does not exist.");
        var targetTagId = tagId ?? rule.TagId;
        if (!await db.Tags.AnyAsync(x => x.Id == targetTagId, cancellationToken))
            throw new McpToolException(McpErrorCodes.NotFound, "Tag does not exist.");
        var mode = suppressMode ?? rule.SuppressMode;
        var suppress = suppressTagId ?? rule.SuppressTagId;
        var before = new AlarmRule
        {
            Id = rule.Id, Key = rule.Key, TagId = rule.TagId, Type = rule.Type, Threshold = rule.Threshold,
            Level = rule.Level, DelaySec = rule.DelaySec, RecoverDeadband = rule.RecoverDeadband,
            Enabled = rule.Enabled, Message = rule.Message, Cause = rule.Cause, Action = rule.Action, OffDelaySec = rule.OffDelaySec,
            SuppressTagId = rule.SuppressTagId, SuppressMode = rule.SuppressMode,
            OutOfService = rule.OutOfService, OutOfServiceReason = rule.OutOfServiceReason
        };
        var updated = new AlarmRule
        {
            Id = rule.Id, Key = rule.Key, TagId = targetTagId, Type = type ?? rule.Type,
            Threshold = threshold ?? rule.Threshold, Level = level ?? rule.Level, DelaySec = delaySec ?? rule.DelaySec,
            RecoverDeadband = recoverDeadband ?? rule.RecoverDeadband, Enabled = enabled ?? rule.Enabled,
            Message = message is null ? rule.Message : (string.IsNullOrWhiteSpace(message) ? null : message.Trim()),
            Cause = cause is null ? rule.Cause : (string.IsNullOrWhiteSpace(cause) ? null : cause.Trim()),
            Action = action is null ? rule.Action : (string.IsNullOrWhiteSpace(action) ? null : action.Trim()),
            OffDelaySec = offDelaySec ?? rule.OffDelaySec,
            SuppressTagId = mode == AlarmSuppressMode.None ? null : suppress,
            SuppressMode = mode, OutOfService = rule.OutOfService, OutOfServiceReason = rule.OutOfServiceReason
        };
        var error = EndpointHelpers.ValidateAlarmRule(updated);
        if (error is not null) return new(ruleId, false, error, targetTagId, ScadaTime.NowMs());
        if (updated.SuppressTagId is int conditionId && !await db.Tags.AnyAsync(x => x.Id == conditionId, cancellationToken))
            return new(ruleId, false, $"条件测点不存在：{conditionId}", targetTagId, ScadaTime.NowMs());
        rule.TagId = updated.TagId;
        rule.Type = updated.Type;
        rule.Threshold = updated.Threshold;
        rule.Level = updated.Level;
        rule.DelaySec = updated.DelaySec;
        rule.RecoverDeadband = updated.RecoverDeadband;
        rule.Enabled = updated.Enabled;
        rule.Message = updated.Message;
        rule.Cause = updated.Cause;
        rule.Action = updated.Action;
        rule.OffDelaySec = updated.OffDelaySec;
        rule.SuppressTagId = updated.SuppressTagId;
        rule.SuppressMode = updated.SuppressMode;
        db.OperationLogs.Add(MasterAudit($"修改报警规则 #{rule.Id}", "AlarmRule", rule.Id, before, rule));
        await db.SaveChangesAsync(cancellationToken);
        await reloader.ReloadAsync(cancellationToken);
        return new(rule.Id, true, "Updated.", rule.TagId, ScadaTime.NowMs());
    }

    [McpServerTool(Name = "delete_alarm_rule", ReadOnly = false, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Delete one alarm rule. Active alarms already raised by the rule are not cleared; history is preserved. Returns success=false when the rule does not exist.")]
    public async Task<McpMasterDeleted> DeleteAlarmRule(int ruleId, CancellationToken cancellationToken)
    {
        if (ruleId <= 0) throw new McpToolException(McpErrorCodes.InvalidArgument, "ruleId must be positive.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rule = await db.AlarmRules.FirstOrDefaultAsync(x => x.Id == ruleId, cancellationToken)
            ?? throw new McpToolException(McpErrorCodes.NotFound, "Alarm rule does not exist.");
        db.AlarmRules.Remove(rule);
        db.OperationLogs.Add(MasterAudit($"删除报警规则 #{rule.Id}：测点 #{rule.TagId}", "AlarmRule", rule.Id, rule, null));
        await db.SaveChangesAsync(cancellationToken);
        await reloader.ReloadAsync(cancellationToken);
        return new(ruleId, true, "Deleted.", ScadaTime.NowMs());
    }

    // ---------- 辅助 ----------

    private static OperationLog MasterAudit(string action, string targetType, object? targetId, object? before, object? after) => new()
    {
        UserName = McpActorName,
        Ts = ScadaTime.NowMs(),
        Action = action,
        TargetType = targetType,
        TargetId = targetId?.ToString(),
        Details = Scada.Server.Auth.Audit.Diff(before, after),
        Result = "成功"
    };

    public sealed record McpAreaSaved(int AreaId, bool Success, string Message, string Name, string? Description, long DataAsOf);
    public sealed record McpDeviceSaved(int DeviceId, bool Success, string Message, string Name, int AreaId, bool Enabled, long DataAsOf);
    public sealed record McpTagSaved(int TagId, bool Success, string Message, string Name, int DeviceId, long DataAsOf);
    public sealed record McpAlarmRuleSaved(int RuleId, bool Success, string Message, int TagId, long DataAsOf);
    public sealed record McpMasterDeleted(int Id, bool Success, string Message, long DataAsOf);
}
