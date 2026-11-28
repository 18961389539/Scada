using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Core.Protocols;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Services;
using Scada.Core;
using Scada.Core.Simulation;
using Scada.Core.Acquisition;
using Scada.Engine.Acquisition;
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
    OpcSecurityPolicy OpcSecurityPolicy = OpcSecurityPolicy.Basic256Sha256, string OpcUserName = "", string? OpcPassword = null, bool ClearOpcPassword = false, int? AreaId = null, string? ProtocolOptions = null,
    bool SimulationEnabled = false, string? SimulationOptions = null, int? ChannelId = null,
    int? StandbyGatewayId = null, int? StandbyChannelId = null, string? StandbyHost = null, int? StandbyPort = null,
    int FailoverHoldMs = 10_000, int SwitchbackHoldMs = 30_000, long? Revision = null);

// 未保存设备的试连：沿用保存时的设备校验，但不落库；网关设备必须保存后由网关执行。
public sealed record DeviceDraftTestRequest(int? Id, DeviceRequest Device, DeviceProbeRequest? Probe = null);
public sealed record DeviceAcquisitionPathRequest(AcquisitionPathKind Path, string? Reason = null);
public sealed record DeviceAcquisitionInfo(int DeviceId, string Name, int AreaId, AcquisitionPathKind ActivePath,
    int? ActiveGatewayId, int? ActiveChannelId, string? ActiveHost, int? ActivePort, bool HasStandby, bool ManualLock, string? Reason, string? ChangedBy, long ChangedTs,
    string? ActiveSerialPort = null);


public static class DeviceEndpoints
{
    // 出站目标白名单属于部署策略，只在保存时读取一次；运行时采集器还会再校验一次。
    internal static Scada.Core.Options.ScadaSettings? Settings(HttpContext http) =>
        http.RequestServices.GetService<Microsoft.Extensions.Options.IOptions<Scada.Core.Options.ScadaSettings>>()?.Value;

    internal static Scada.Core.Interfaces.IProtocolDriverAvailability? Drivers(HttpContext http) =>
        http.RequestServices.GetService<Scada.Core.Interfaces.IProtocolDriverAvailability>();

    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").AddEndpointFilter<AuditFailuresFilter>();

        group.MapGet("/acquisition", async (
            HttpContext http,
            IDeviceAcquisitionPaths paths,
            IDbContextFactory<ConfigDbContext> configFactory,
            IDbContextFactory<RuntimeDbContext> runtimeFactory,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await configFactory.CreateDbContextAsync(cancellationToken);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var devices = (await db.Devices.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken))
                .Where(x => access.Has(Permission.View, x.AreaId)).ToList();
            var states = await runtime.DeviceAcquisitionStates.AsNoTracking().ToDictionaryAsync(x => x.DeviceId, cancellationToken);
            return Results.Ok(devices.Select(device => ToAcquisitionInfo(device, paths, states.GetValueOrDefault(device.Id))));
        }).RequireAuthorization();

        group.MapGet("/{id:int}/acquisition", async (
            int id,
            HttpContext http,
            IDeviceAcquisitionPaths paths,
            IDbContextFactory<ConfigDbContext> configFactory,
            IDbContextFactory<RuntimeDbContext> runtimeFactory,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointHelpers.RequireDeviceAsync(http, id, Permission.View, $"查看设备采集路径 #{id}") is { } denied) return denied;
            await using var db = await configFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (device is null) return Results.NotFound();
            await using var runtime = await runtimeFactory.CreateDbContextAsync(cancellationToken);
            var state = await runtime.DeviceAcquisitionStates.AsNoTracking().SingleOrDefaultAsync(x => x.DeviceId == id, cancellationToken);
            return Results.Ok(ToAcquisitionInfo(device, paths, state));
        }).RequireAuthorization();

        group.MapPost("/{id:int}/acquisition-path", async (
            int id,
            DeviceAcquisitionPathRequest request,
            HttpContext http,
            DeviceAcquisitionPathService paths,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointHelpers.RequireDeviceAsync(http, id, Permission.Control, $"切换设备采集路径 #{id}") is { } denied) return denied;
            try
            {
                var user = http.User.Identity?.Name ?? "user";
                await paths.SwitchManualAsync(id, request.Path, request.Reason ?? "", user, cancellationToken);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return EndpointHelpers.ValidationProblem([EndpointHelpers.Issue("device.acquisition.switch", "path", ex.Message)]);
            }
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapPost("/{id:int}/acquisition-path/auto", async (
            int id,
            HttpContext http,
            DeviceAcquisitionPathService paths,
            CancellationToken cancellationToken) =>
        {
            if (await EndpointHelpers.RequireDeviceAsync(http, id, Permission.Control, $"恢复设备采集自动回切 #{id}") is { } denied) return denied;
            try
            {
                var user = http.User.Identity?.Name ?? "user";
                await paths.ResumeAutoAsync(id, user, cancellationToken);
                return Results.Ok();
            }
            catch (InvalidOperationException ex)
            {
                return EndpointHelpers.ValidationProblem([EndpointHelpers.Issue("device.acquisition.resume", "path", ex.Message)]);
            }
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapGet("", async (
            string? search,
            string? protocol,
            string? enabled,
            int? gatewayId,
            int? skip,
            int? take,
            string? sort,
            string? order,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var paths = http.RequestServices.GetService<IDeviceAcquisitionPaths>() ?? PrimaryDeviceAcquisitionPaths.Instance;
            // take 省略时保持原行为：一次返回调用方可见的全部设备，已有 list() 不用改。
            if (take is not int requestedTake)
            {
                var devices = await db.Devices.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken);
                var visible = devices.Where(x => access.Has(Permission.View, x.AreaId)).ToList();
                if (gatewayId is int unpagedGateway)
                    visible = visible.Where(x => paths.Resolve(x).GatewayId == unpagedGateway).ToList();
                foreach (var device in visible) StampListRoute(device, paths, gatewayId);
                return Results.Ok(visible);
            }

            var scoped = db.Devices.AsNoTracking().AsQueryable();
            if (access.AreasWith(Permission.View) is { } areas)
            {
                if (areas.Count == 0)
                {
                    http.Response.Headers["X-Total-Count"] = "0";
                    http.Response.Headers["X-Device-Protocols"] = "";
                    return Results.Ok(Array.Empty<Device>());
                }

                var allowed = areas.ToArray();
                scoped = scoped.Where(x => allowed.Contains(x.AreaId));
            }

            var protocols = await scoped.Select(x => x.Protocol).Distinct().ToListAsync(cancellationToken);
            http.Response.Headers["X-Device-Protocols"] = string.Join(',',
                protocols.Select(item => item.ToString()).OrderBy(item => item, StringComparer.Ordinal));

            var filtered = scoped;
            if (!string.IsNullOrWhiteSpace(search))
            {
                var keyword = search.Trim();
                filtered = filtered.Where(x =>
                    x.Name.Contains(keyword) ||
                    x.Host.Contains(keyword) ||
                    x.SerialPort.Contains(keyword) ||
                    x.OpcEndpointUrl.Contains(keyword) ||
                    x.Port.ToString().Contains(keyword));
            }

            if (!IsAll(protocol))
            {
                if (Enum.TryParse(protocol, ignoreCase: false, out ProtocolType parsedProtocol) && parsedProtocol.ToString() == protocol)
                    filtered = filtered.Where(x => x.Protocol == parsedProtocol);
                else
                    filtered = filtered.Where(_ => false);
            }

            if (!IsAll(enabled))
            {
                if (enabled == "enabled") filtered = filtered.Where(x => x.Enabled);
                else if (enabled == "disabled") filtered = filtered.Where(x => !x.Enabled);
                else filtered = filtered.Where(_ => false);
            }

            if (gatewayId is int gateway)
                filtered = filtered.Where(x => x.GatewayId == gateway || x.StandbyGatewayId == gateway);

            IReadOnlyList<Device> page;
            int total;
            if (gatewayId is int activeGateway)
            {
                var matches = FilterByActiveGateway(await OrderDevices(filtered, sort, order).ToListAsync(cancellationToken), paths, activeGateway);
                total = matches.Count;
                var pageSize = Math.Clamp(requestedTake, 1, 500);
                page = matches.Skip(Math.Max(0, skip ?? 0)).Take(pageSize).ToList();
            }
            else
            {
                total = await filtered.CountAsync(cancellationToken);
                var pageSize = Math.Clamp(requestedTake, 1, 500);
                page = await OrderDevices(filtered, sort, order)
                    .Skip(Math.Max(0, skip ?? 0))
                    .Take(pageSize)
                    .ToListAsync(cancellationToken);
                foreach (var device in page) StampListRoute(device, paths, null);
            }
            http.Response.Headers["X-Total-Count"] = total.ToString();
            return Results.Ok(page);
        }).Produces<List<Device>>(StatusCodes.Status200OK);

        group.MapGet("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            CancellationToken cancellationToken) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return device is null || !access.Has(Permission.View, device.AreaId) ? Results.NotFound() : Results.Ok(device);
        }).Produces<Device>(StatusCodes.Status200OK);

        group.MapPost("", async (
            DeviceRequest request,
            HttpContext http,
            IDeviceCredentials credentials,
            IDbContextFactory<ConfigDbContext> contextFactory,
            DeviceCommandService deviceCommands,
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
                ChannelId = request.ChannelId,
                StationNumber = request.StationNumber,
                ByteOrder = request.ByteOrder,
                PollIntervalMs = request.PollIntervalMs,
                TimeoutMs = request.TimeoutMs,
                Enabled = request.Enabled,
                DisabledSinceTs = request.Enabled ? 0 : ScadaTime.NowMs(),
                SerialPort = request.SerialPort?.Trim() ?? "", BaudRate = request.BaudRate, DataBits = request.DataBits,
                Parity = request.Parity, StopBits = request.StopBits, Cpu = request.Cpu, Rack = request.Rack, Slot = request.Slot
            };
            ApplyStandbySettings(device, request);
            var credentialError = ApplyOpcSettings(device, request, credentials);
            if (credentialError is not null)
                return EndpointHelpers.ValidationProblem(
                    [EndpointHelpers.Issue("device.opc.password", nameof(request.OpcPassword), credentialError)]);
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            // 审计行由 AuditInterceptor 在保存后自动写入（Device 实现了 IAuditedEntity）。
            var result = await deviceCommands.CreateAsync(db, device, cancellationToken);
            return result.Success
                ? Results.Created($"/api/devices/{device.Id}", device)
                : EndpointHelpers.ValidationProblem(result.Issues.Select(issue => issue.ToApiIssue()).ToArray(), result.Message);
        }).Produces<Device>(StatusCodes.Status201Created).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id,
            DeviceRequest request,
            HttpContext http,
            IDeviceCredentials credentials,
            IDbContextFactory<ConfigDbContext> contextFactory,
            DeviceCommandService deviceCommands,
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
                if (!await db.Areas.AnyAsync(x => x.Id == newArea, cancellationToken))
                    return EndpointHelpers.ValidationProblem(
                        [EndpointHelpers.Issue("device.area.notFound", nameof(request.AreaId), "区域不存在")]);
            }

            if (request.AreaId is int targetArea) device.AreaId = targetArea;
            var name = request.Name?.Trim() ?? "";
            var host = request.Host?.Trim() ?? "";
            // 重名检查统一在 DeviceCommandService 里做，这里不再先查一遍。
            device.Name = name;
            device.Protocol = request.Protocol;
            device.Host = host;
            device.Port = request.Port;
            device.ChannelId = request.ChannelId;
            device.StationNumber = request.StationNumber;
            device.ByteOrder = request.ByteOrder;
            device.PollIntervalMs = request.PollIntervalMs;
            device.TimeoutMs = request.TimeoutMs;
            if (device.Enabled && !request.Enabled) device.DisabledSinceTs = ScadaTime.NowMs();
            device.Enabled = request.Enabled;
            device.SerialPort = request.SerialPort?.Trim() ?? ""; device.BaudRate = request.BaudRate; device.DataBits = request.DataBits;
            device.Parity = request.Parity; device.StopBits = request.StopBits; device.Cpu = request.Cpu; device.Rack = request.Rack; device.Slot = request.Slot;
            ApplyStandbySettings(device, request);
            var credentialError = ApplyOpcSettings(device, request, credentials);
            if (credentialError is not null)
                return EndpointHelpers.ValidationProblem(
                    [EndpointHelpers.Issue("device.opc.password", nameof(request.OpcPassword), credentialError)]);

            var result = await deviceCommands.UpdateAsync(db, device, cancellationToken, request.Revision);
            if (result.Error == DeviceCommandError.Conflict)
                return Results.Conflict(new { message = result.Message ?? Scada.Core.Configuration.ConfigRevision.ConflictMessage, issues = result.Issues.Select(issue => issue.ToApiIssue()).ToArray() });
            return result.Success
                ? Results.Ok(device)
                : EndpointHelpers.ValidationProblem(result.Issues.Select(issue => issue.ToApiIssue()).ToArray(), result.Message);
        }).Produces<Device>(StatusCodes.Status200OK).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (
            int id,
            HttpContext http,
            IDbContextFactory<ConfigDbContext> contextFactory,
            DeviceCommandService deviceCommands,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var device = await db.Devices.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (device is null)
            {
                return Results.NotFound();
            }

            if (await EndpointHelpers.RequireDeviceAsync(http, id, Permission.Configure, $"删除设备 #{id}") is { } denied) return denied;
            var result = await deviceCommands.DeleteAsync(db, device, cancellationToken);
            if (result.Success) return Results.NoContent();
            return result.Error == DeviceCommandError.Conflict
                ? Results.Conflict(new { message = result.Message, issues = result.Issues.Select(issue => issue.ToApiIssue()).ToArray() })
                : EndpointHelpers.ValidationProblem(result.Issues.Select(issue => issue.ToApiIssue()).ToArray(), result.Message);
        }).Produces(StatusCodes.Status204NoContent).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/{id:int}/test", (
            int id,
            DeviceProbeRequest? request,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            IDeviceDriverFactory driverFactory,
            IReadPlanBuilder planBuilder,
            ICollectorControl collector,
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
            var paths = http.RequestServices.GetService<IDeviceAcquisitionPaths>() ?? PrimaryDeviceAcquisitionPaths.Instance;
            var live = paths.Apply(device);
            if (paths.Resolve(device).GatewayId != null) return Results.Ok(await gateways.EngineerAsync<DeviceProbeResult>(id, GatewayEngineeringOperation.Probe, request ?? new DeviceProbeRequest(), http.User.GetUserId()!.Value, cancellationToken));
            if (Settings(http) is { LocalCollectionEnabled: false })
                return Results.Conflict(new { message = LocalCollection.DisabledMessage });
            try
            {
                return Results.Ok(await DeviceProbe.ReadAsync(live, request, driverFactory, planBuilder, cancellationToken, collector));
            }
            catch (ArgumentException ex) { return EndpointHelpers.BadRequest(ex.Message); }
        })).Produces<DeviceProbeResult>(StatusCodes.Status200OK).RequireAuthorization(AccessPolicies.Control);

        group.MapPost("/test-draft", async (
            DeviceDraftTestRequest request,
            HttpContext http,
            IDeviceCredentials credentials,
            IDbContextFactory<ConfigDbContext> contextFactory, IDbContextFactory<RuntimeDbContext> runtimeFactory,
            IDeviceDriverFactory driverFactory,
            IReadPlanBuilder planBuilder,
            DeviceCommandService deviceCommands,
            CancellationToken cancellationToken) =>
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            Device? stored = null;
            if (request.Id is int id)
            {
                stored = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            }

            if (stored is not null)
            {
                // 编辑场景必须同时具备已保存设备所在区域的配置与控制权，避免借用其已保存凭据发起试连。
                if (await EndpointHelpers.RequireAreaAsync(http, stored.AreaId, Permission.Configure, $"测试设备 #{stored.Id}") is { } deniedStoredConfigure) return deniedStoredConfigure;
                if (await EndpointHelpers.RequireAreaAsync(http, stored.AreaId, Permission.Control, $"测试设备 #{stored.Id}") is { } deniedStoredControl) return deniedStoredControl;
            }

            if (stored is not null && (http.RequestServices.GetService<IDeviceAcquisitionPaths>() ?? PrimaryDeviceAcquisitionPaths.Instance).Resolve(stored).GatewayId != null)
            {
                return EndpointHelpers.BadRequest("该设备由网关采集，请保存后通过网关诊断测试连接");
            }

            var areaId = request.Device.AreaId ?? stored?.AreaId ?? Area.DefaultId;
            if (await EndpointHelpers.RequireAreaAsync(http, areaId, Permission.Configure, $"测试未保存设备：{request.Device.Name}") is { } deniedConfigure) return deniedConfigure;
            if (await EndpointHelpers.RequireAreaAsync(http, areaId, Permission.Control, $"测试未保存设备：{request.Device.Name}") is { } deniedControl) return deniedControl;

            if (Settings(http) is { LocalCollectionEnabled: false })
                return Results.Conflict(new { message = LocalCollection.DisabledMessage });

            var device = new Device
            {
                // 新设备尚无主键；试连探针校验要求正的设备号，这里用占位值且不落库。
                Id = request.Id ?? 1,
                AreaId = areaId,
                Name = request.Device.Name?.Trim() ?? "",
                Protocol = request.Device.Protocol,
                Host = request.Device.Host?.Trim() ?? "",
                Port = request.Device.Port,
                StationNumber = request.Device.StationNumber,
                ByteOrder = request.Device.ByteOrder,
                PollIntervalMs = request.Device.PollIntervalMs,
                TimeoutMs = request.Device.TimeoutMs,
                Enabled = request.Device.Enabled,
                DisabledSinceTs = 0,
                SerialPort = request.Device.SerialPort?.Trim() ?? "", BaudRate = request.Device.BaudRate, DataBits = request.Device.DataBits,
                Parity = request.Device.Parity, StopBits = request.Device.StopBits, Cpu = request.Device.Cpu, Rack = request.Device.Rack, Slot = request.Device.Slot,
                ChannelId = request.Device.ChannelId
            };
            ApplyStandbySettings(device, request.Device);
            var credentialError = ApplyOpcSettings(device, request.Device, credentials);
            if (credentialError is not null)
                return EndpointHelpers.ValidationProblem(
                    [EndpointHelpers.Issue("device.opc.password", nameof(request.Device.OpcPassword), credentialError)]);
            // 试连按通道端点发起：否则用户改了通道地址再点"测试"会打到旧地址。
            var channelIssues = await DeviceCommandService.ApplyChannelEndpointIssuesAsync(db, device, cancellationToken);
            if (channelIssues.Count > 0)
                return EndpointHelpers.ValidationProblem(channelIssues.Select(issue => issue.ToApiIssue()).ToArray());
            var standbyChannelIssues = await DeviceCommandService.ApplyStandbyChannelEndpointIssuesAsync(db, device, cancellationToken);
            if (standbyChannelIssues.Count > 0)
                return EndpointHelpers.ValidationProblem(standbyChannelIssues.Select(issue => issue.ToApiIssue()).ToArray());

            // 编辑时留空密码表示沿用已保存口令；试连也不要求用户重新输入。
            var usesCredentials = device.Protocol == ProtocolType.OpcUa
                || (ProtocolCatalog.TryGet(device.Protocol, out var spec) && spec.UsesCredentials);
            if (usesCredentials && device.OpcPasswordProtected is null && !request.Device.ClearOpcPassword && stored?.OpcPasswordProtected is not null)
            {
                device.OpcPasswordProtected = stored.OpcPasswordProtected;
            }

            var deviceIssues = ConfigurationValidator.DeviceIssues(device, Settings(http), Drivers(http));
            if (deviceIssues.Count > 0)
            {
                return EndpointHelpers.ValidationProblem(deviceIssues.Select(issue => issue.ToApiIssue()).ToArray());
            }

            try
            {
                if (request.Probe is null && (ProtocolCatalog.IsTextAddress(device.Protocol) || device.Protocol == ProtocolType.OpcUa))
                {
                    return Results.Ok(await DeviceProbe.ConnectAsync(device, driverFactory, cancellationToken));
                }

                return Results.Ok(await DeviceProbe.ReadAsync(device, request.Probe, driverFactory, planBuilder, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return EndpointHelpers.BadRequest(ex.Message);
            }
        }).Produces<DeviceProbeResult>(StatusCodes.Status200OK).RequireAuthorization(AccessPolicies.Control);
    }

    private static bool IsAll(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.Ordinal);

    private static IOrderedQueryable<Device> OrderDevices(IQueryable<Device> query, string? sort, string? order)
    {
        var descending = string.Equals(order, "descending", StringComparison.Ordinal);
        return sort switch
        {
            "name" => descending ? query.OrderByDescending(x => x.Name).ThenBy(x => x.Id) : query.OrderBy(x => x.Name).ThenBy(x => x.Id),
            "stationNumber" => descending ? query.OrderByDescending(x => x.StationNumber).ThenBy(x => x.Id) : query.OrderBy(x => x.StationNumber).ThenBy(x => x.Id),
            "pollIntervalMs" => descending ? query.OrderByDescending(x => x.PollIntervalMs).ThenBy(x => x.Id) : query.OrderBy(x => x.PollIntervalMs).ThenBy(x => x.Id),
            "timeoutMs" => descending ? query.OrderByDescending(x => x.TimeoutMs).ThenBy(x => x.Id) : query.OrderBy(x => x.TimeoutMs).ThenBy(x => x.Id),
            "id" when descending => query.OrderByDescending(x => x.Id),
            _ => query.OrderBy(x => x.Id)
        };
    }

    internal static List<Device> FilterByActiveGateway(IEnumerable<Device> devices, IDeviceAcquisitionPaths paths, int gatewayId)
    {
        var matches = devices.Where(x => paths.Resolve(x).GatewayId == gatewayId).ToList();
        foreach (var device in matches) StampListRoute(device, paths, gatewayId);
        return matches;
    }

    private static void StampListRoute(Device device, IDeviceAcquisitionPaths paths, int? filteredGatewayId)
    {
        var route = paths.Resolve(device);
        device.ActiveGatewayId = route.GatewayId;
        device.ConfiguredStandby = filteredGatewayId is int gateway
            && device.StandbyGatewayId == gateway && device.GatewayId != gateway;
    }

    private static DeviceAcquisitionInfo ToAcquisitionInfo(Device device, IDeviceAcquisitionPaths paths, DeviceAcquisitionState? state)
    {
        var route = paths.Resolve(device);
        var applied = paths.Apply(device);
        return new DeviceAcquisitionInfo(
            device.Id, device.Name, device.AreaId, route.Path, route.GatewayId, route.ChannelId, route.Host, route.Port,
            DeviceAcquisitionRules.HasStandby(device), state?.ManualLock == true, state?.Reason, state?.ChangedBy, state?.ChangedTs ?? 0,
            string.IsNullOrWhiteSpace(applied.SerialPort) ? null : applied.SerialPort);
    }

    internal static void ApplyStandbySettings(Device device, DeviceRequest request)
    {
        device.StandbyGatewayId = request.StandbyGatewayId;
        device.StandbyChannelId = request.StandbyChannelId;
        device.StandbyHost = string.IsNullOrWhiteSpace(request.StandbyHost) ? null : request.StandbyHost.Trim();
        device.StandbyPort = request.StandbyPort;
        device.FailoverHoldMs = request.FailoverHoldMs;
        device.SwitchbackHoldMs = request.SwitchbackHoldMs;
    }

    internal static string? ApplyOpcSettings(Device device, DeviceRequest request, IDeviceCredentials credentials)
    {
        if (request.OpcPassword?.Length > 4096) return "OPC UA 密码过长";
        device.OpcEndpointUrl = request.OpcEndpointUrl?.Trim() ?? ""; device.OpcUserName = request.OpcUserName?.Trim() ?? "";
        device.ProtocolOptions = request.ProtocolOptions?.Trim() ?? "";
        device.SimulationEnabled = request.SimulationEnabled;
        device.SimulationOptions = request.SimulationOptions?.Trim() ?? "";
        device.OpcSecurityMode = request.OpcSecurityMode; device.OpcSecurityPolicy = request.OpcSecurityPolicy;
        if (request.ClearOpcPassword) device.OpcPasswordProtected = null;
        else if (request.OpcPassword is not null) device.OpcPasswordProtected = credentials.Protect(request.OpcPassword);
        return null;
    }

}
