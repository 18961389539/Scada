using Microsoft.EntityFrameworkCore;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Security;
using Scada.Data;
using Scada.Server.Auth;

namespace Scada.Server.Endpoints;

public sealed record AssetRequest(
    string Code, string Name, string? Category, string? Model, string? Manufacturer, string? SerialNumber,
    string? Location, int? AreaId, int? DeviceId, AssetStatus Status, long? CommissionedTs, long? WarrantyUntilTs,
    string? Description, bool Enabled = true);

public sealed record WorkOrderRequest(
    string Title, int? AssetId, int? DeviceId, int? AreaId, WorkOrderType Type, WorkOrderPriority Priority,
    string? Description, string? Assignee, string? Reporter, long? PlannedStartTs, long? PlannedEndTs);

public sealed record WorkOrderStatusRequest(WorkOrderStatus Status, string? Resolution, int? DowntimeMinutes, string? Note);

public sealed record WorkOrderPartRequest(int PartId, double Quantity, string? Note);

public sealed record SparePartRequest(
    string Code, string Name, string? Specification, string? Unit, double Stock, double MinStock,
    string? Location, string? Supplier, double? UnitPrice, string? Description, bool Enabled = true);

public sealed record SparePartMovementRequest(SparePartMovementType Type, double Quantity, string? Note, int? WorkOrderId);

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        MapAssets(app);
        MapWorkOrders(app);
        MapSpareParts(app);
    }

    private static void MapAssets(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets").AddEndpointFilter<AuditFailuresFilter>().RequireAuthorization(AccessPolicies.View);

        group.MapGet("", async (
            string? search, AssetStatus? status, int? areaId, int? deviceId, bool? enabled,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.Assets.AsNoTracking();
            if (status is AssetStatus value) query = query.Where(x => x.Status == value);
            if (areaId is int area) query = query.Where(x => x.AreaId == area);
            if (deviceId is int device) query = query.Where(x => x.DeviceId == device);
            if (enabled is bool flag) query = query.Where(x => x.Enabled == flag);
            var rows = await query.OrderBy(x => x.Code).Take(1000).ToListAsync(ct);
            var keyword = search?.Trim();
            if (keyword is { Length: > 0 })
                rows = rows.Where(x => $"{x.Code} {x.Name} {x.Category} {x.Model} {x.SerialNumber} {x.Location}".Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Ok(rows.Where(x => access.Has(Permission.View, x.AreaId)).ToList());
        });

        group.MapPost("", async (
            AssetRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var areaId = request.AreaId ?? Area.DefaultId;
            if (request.DeviceId is int deviceId)
            {
                var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
                if (device is null) return EndpointHelpers.BadRequest("关联采集设备不存在");
                if (request.AreaId is null) areaId = device.AreaId;
            }
            if (await EndpointHelpers.RequireAreaAsync(http, areaId, Permission.Configure, $"创建设备资产：{request.Name}") is { } denied) return denied;
            var asset = new Asset
            {
                Code = request.Code?.Trim() ?? "", Name = request.Name?.Trim() ?? "", Category = request.Category?.Trim() ?? "",
                Model = request.Model?.Trim() ?? "", Manufacturer = request.Manufacturer?.Trim() ?? "", SerialNumber = request.SerialNumber?.Trim() ?? "",
                Location = request.Location?.Trim() ?? "", AreaId = areaId, DeviceId = request.DeviceId, Status = request.Status,
                CommissionedTs = request.CommissionedTs, WarrantyUntilTs = request.WarrantyUntilTs,
                Description = request.Description?.Trim() ?? "", Enabled = request.Enabled,
                CreatedTs = ScadaTime.NowMs(), UpdatedTs = ScadaTime.NowMs(), UpdatedBy = http.User.GetUserName()
            };
            var error = AssetValidation.Asset(asset);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.Assets.AnyAsync(x => x.Code == asset.Code, ct)) return EndpointHelpers.BadRequest("资产编号已存在");
            db.Assets.Add(asset);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("资产编号已存在"); }
            runtime.OperationLogs.Add(Audit.Change(http, $"创建设备资产：{asset.Name}", "Asset", asset.Id, null, asset));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Created($"/api/assets/{asset.Id}", asset);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id, AssetRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var asset = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (asset is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, asset.AreaId, Permission.Configure, $"修改设备资产 #{id}") is { } denied) return denied;
            var before = EndpointHelpers.JsonCopy(asset);
            var areaId = request.AreaId ?? asset.AreaId;
            if (areaId != asset.AreaId && await EndpointHelpers.RequireAreaAsync(http, areaId, Permission.Configure, $"移动设备资产 #{id}") is { } deniedArea) return deniedArea;
            if (request.DeviceId is int deviceId)
            {
                var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct);
                if (device is null) return EndpointHelpers.BadRequest("关联采集设备不存在");
                if (request.AreaId is null) areaId = device.AreaId;
            }
            asset.Code = request.Code?.Trim() ?? ""; asset.Name = request.Name?.Trim() ?? ""; asset.Category = request.Category?.Trim() ?? "";
            asset.Model = request.Model?.Trim() ?? ""; asset.Manufacturer = request.Manufacturer?.Trim() ?? ""; asset.SerialNumber = request.SerialNumber?.Trim() ?? "";
            asset.Location = request.Location?.Trim() ?? ""; asset.AreaId = areaId; asset.DeviceId = request.DeviceId; asset.Status = request.Status;
            asset.CommissionedTs = request.CommissionedTs; asset.WarrantyUntilTs = request.WarrantyUntilTs;
            asset.Description = request.Description?.Trim() ?? ""; asset.Enabled = request.Enabled;
            asset.UpdatedTs = ScadaTime.NowMs(); asset.UpdatedBy = http.User.GetUserName();
            var error = AssetValidation.Asset(asset);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.Assets.AnyAsync(x => x.Id != id && x.Code == asset.Code, ct)) return EndpointHelpers.BadRequest("资产编号已存在");
            runtime.OperationLogs.Add(Audit.Change(http, $"修改设备资产：{asset.Name}", "Asset", asset.Id, before, asset));
            try { await db.SaveChangesAsync(ct); await runtime.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("资产编号已存在"); }
            return Results.Ok(asset);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var asset = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (asset is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, asset.AreaId, Permission.Configure, $"删除设备资产 #{id}") is { } denied) return denied;
            if (await db.MaintenanceWorkOrders.AnyAsync(x => x.AssetId == id && (x.Status == WorkOrderStatus.Open || x.Status == WorkOrderStatus.InProgress || x.Status == WorkOrderStatus.OnHold), ct))
                return EndpointHelpers.BadRequest("该资产仍有未关闭的维护工单，请先完成或取消工单");
            db.Assets.Remove(asset);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除设备资产：{asset.Name}", "Asset", asset.Id, asset, null));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);
    }

    private static void MapWorkOrders(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-orders").AddEndpointFilter<AuditFailuresFilter>().RequireAuthorization(AccessPolicies.View);

        group.MapGet("", async (
            string? search, WorkOrderStatus? status, WorkOrderType? type, WorkOrderPriority? priority, int? assetId, int? areaId, int? take,
            HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var access = await http.GetAccessAsync();
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.MaintenanceWorkOrders.AsNoTracking();
            if (status is WorkOrderStatus value) query = query.Where(x => x.Status == value);
            if (type is WorkOrderType valueType) query = query.Where(x => x.Type == valueType);
            if (priority is WorkOrderPriority valuePriority) query = query.Where(x => x.Priority == valuePriority);
            if (assetId is int asset) query = query.Where(x => x.AssetId == asset);
            if (areaId is int area) query = query.Where(x => x.AreaId == area);
            var rows = await query.OrderByDescending(x => x.CreatedTs).Take(Math.Clamp(take ?? 500, 1, 1000)).ToListAsync(ct);
            var keyword = search?.Trim();
            if (keyword is { Length: > 0 })
                rows = rows.Where(x => $"{x.Number} {x.Title} {x.Assignee} {x.Reporter}".Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Ok(rows.Where(x => access.Has(Permission.View, x.AreaId)).ToList());
        });

        group.MapPost("", async (
            WorkOrderRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var resolved = await ResolveWorkOrderAreaAsync(db, request.AreaId, request.AssetId, request.DeviceId, ct);
            if (resolved.Error is not null) return EndpointHelpers.BadRequest(resolved.Error);
            var areaId = resolved.AreaId;
            if (await EndpointHelpers.RequireAreaAsync(http, areaId, Permission.Control, $"创建维护工单：{request.Title}") is { } denied) return denied;
            var now = ScadaTime.NowMs();
            var order = new MaintenanceWorkOrder
            {
                Number = AssetValidation.NewWorkOrderNumber(now), Title = request.Title?.Trim() ?? "",
                AssetId = request.AssetId, DeviceId = request.DeviceId, AreaId = areaId, Type = request.Type, Priority = request.Priority,
                Status = WorkOrderStatus.Open, Description = request.Description?.Trim() ?? "", Assignee = request.Assignee?.Trim() ?? "",
                Reporter = request.Reporter?.Trim() ?? "", PlannedStartTs = request.PlannedStartTs, PlannedEndTs = request.PlannedEndTs,
                CreatedTs = now, UpdatedTs = now, UpdatedBy = http.User.GetUserName()
            };
            var error = AssetValidation.WorkOrder(order);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            db.MaintenanceWorkOrders.Add(order);
            await db.SaveChangesAsync(ct);
            runtime.OperationLogs.Add(Audit.Change(http, $"创建维护工单：{order.Number}", "MaintenanceWorkOrder", order.Id, null, order));
            await runtime.SaveChangesAsync(ct);
            return Results.Created($"/api/work-orders/{order.Id}", order);
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapPut("/{id:int}", async (
            int id, WorkOrderRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var order = await db.MaintenanceWorkOrders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (order is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, order.AreaId, Permission.Configure, $"修改维护工单 #{id}") is { } denied) return denied;
            if (order.Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled) return EndpointHelpers.BadRequest("已完成或已取消的工单不能修改");
            var resolved = await ResolveWorkOrderAreaAsync(db, request.AreaId, request.AssetId, request.DeviceId, ct);
            if (resolved.Error is not null) return EndpointHelpers.BadRequest(resolved.Error);
            if (resolved.AreaId != order.AreaId && await EndpointHelpers.RequireAreaAsync(http, resolved.AreaId, Permission.Configure, $"移动维护工单 #{id}") is { } deniedMove) return deniedMove;
            var before = EndpointHelpers.JsonCopy(order);
            order.Title = request.Title?.Trim() ?? ""; order.AssetId = request.AssetId; order.DeviceId = request.DeviceId; order.AreaId = resolved.AreaId;
            order.Type = request.Type; order.Priority = request.Priority; order.Description = request.Description?.Trim() ?? "";
            order.Assignee = request.Assignee?.Trim() ?? ""; order.Reporter = request.Reporter?.Trim() ?? "";
            order.PlannedStartTs = request.PlannedStartTs; order.PlannedEndTs = request.PlannedEndTs;
            order.UpdatedTs = ScadaTime.NowMs(); order.UpdatedBy = http.User.GetUserName();
            var error = AssetValidation.WorkOrder(order);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            runtime.OperationLogs.Add(Audit.Change(http, $"修改维护工单：{order.Number}", "MaintenanceWorkOrder", order.Id, before, order));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Ok(order);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPost("/{id:int}/status", async (
            int id, WorkOrderStatusRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var order = await db.MaintenanceWorkOrders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (order is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, order.AreaId, Permission.Control, $"更新维护工单状态 #{id}") is { } denied) return denied;
            if (!Enum.IsDefined(request.Status)) return EndpointHelpers.BadRequest("工单状态无效");
            if (order.Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled) return EndpointHelpers.BadRequest("已完成或已取消的工单不能变更状态");
            var before = EndpointHelpers.JsonCopy(order);
            var now = ScadaTime.NowMs();
            if (request.Status == WorkOrderStatus.InProgress && order.StartedTs is null) order.StartedTs = now;
            if (request.Status == WorkOrderStatus.Completed)
            {
                var resolution = request.Resolution?.Trim();
                if (string.IsNullOrEmpty(resolution)) return EndpointHelpers.BadRequest("完成工单必须填写处理结果");
                order.Resolution = resolution;
                order.CompletedTs = now;
            }
            else if (request.Status == WorkOrderStatus.Cancelled) order.CompletedTs = now;
            if (request.DowntimeMinutes is int downtime) order.DowntimeMinutes = downtime;
            order.Status = request.Status; order.UpdatedTs = now; order.UpdatedBy = http.User.GetUserName();
            var error = AssetValidation.WorkOrder(order);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            runtime.OperationLogs.Add(Audit.Change(http, $"维护工单状态：{order.Number} → {order.Status}", "MaintenanceWorkOrder", order.Id, before, order));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            await SyncAssetStatusAsync(db, order.AssetId, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(order);
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapPost("/{id:int}/parts", async (
            int id, WorkOrderPartRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            if (!double.IsFinite(request.Quantity) || request.Quantity <= 0) return EndpointHelpers.BadRequest("领用数量须大于 0");
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var order = await db.MaintenanceWorkOrders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (order is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, order.AreaId, Permission.Control, $"工单领用备件 #{id}") is { } denied) return denied;
            if (order.Status is WorkOrderStatus.Completed or WorkOrderStatus.Cancelled) return EndpointHelpers.BadRequest("已完成或已取消的工单不能领用备件");
            var part = await db.SpareParts.FirstOrDefaultAsync(x => x.Id == request.PartId, ct);
            if (part is null || !part.Enabled) return EndpointHelpers.BadRequest("备件不存在或已停用");
            if (part.Stock < request.Quantity) return EndpointHelpers.BadRequest($"备件库存不足，当前库存 {part.Stock:0.###} {part.Unit}".TrimEnd());
            var now = ScadaTime.NowMs();
            part.Stock -= request.Quantity; part.UpdatedTs = now;
            db.SparePartMovements.Add(new SparePartMovement
            {
                PartId = part.Id, Type = SparePartMovementType.Out, Quantity = request.Quantity, BalanceAfter = part.Stock,
                WorkOrderId = order.Id, Operator = http.User.GetUserName(), Note = request.Note?.Trim() ?? "", Ts = now
            });
            runtime.OperationLogs.Add(Audit.Change(http, $"工单领用备件：{order.Number} / {part.Name} × {request.Quantity:0.###}",
                "SparePartMovement", part.Id, new { stock = part.Stock + request.Quantity }, new { stock = part.Stock, orderId = order.Id }));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Ok(new { partId = part.Id, stock = part.Stock, balanceAfter = part.Stock });
        }).RequireAuthorization(AccessPolicies.Control);

        group.MapGet("/{id:int}/parts", async (
            int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var order = await db.MaintenanceWorkOrders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (order is null || !(await http.GetAccessAsync()).Has(Permission.View, order.AreaId)) return Results.NotFound();
            var rows = await db.SparePartMovements.AsNoTracking().Where(x => x.WorkOrderId == id).OrderByDescending(x => x.Ts).ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var order = await db.MaintenanceWorkOrders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (order is null) return Results.NotFound();
            if (await EndpointHelpers.RequireAreaAsync(http, order.AreaId, Permission.Configure, $"删除维护工单 #{id}") is { } denied) return denied;
            if (order.Status is not (WorkOrderStatus.Open or WorkOrderStatus.Cancelled)) return EndpointHelpers.BadRequest("只有未开始或已取消的工单可以删除");
            db.MaintenanceWorkOrders.Remove(order);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除维护工单：{order.Number}", "MaintenanceWorkOrder", order.Id, order, null));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);
    }

    private static void MapSpareParts(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spare-parts").AddEndpointFilter<AuditFailuresFilter>().RequireAuthorization(AccessPolicies.View);

        group.MapGet("", async (
            string? search, bool? lowStock, bool? enabled, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var query = db.SpareParts.AsNoTracking();
            if (enabled is bool flag) query = query.Where(x => x.Enabled == flag);
            var rows = await query.OrderBy(x => x.Code).ToListAsync(ct);
            if (lowStock is true) rows = rows.Where(x => x.Stock < x.MinStock).ToList();
            var keyword = search?.Trim();
            if (keyword is { Length: > 0 })
                rows = rows.Where(x => $"{x.Code} {x.Name} {x.Specification} {x.Location} {x.Supplier}".Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
            return Results.Ok(rows);
        });

        group.MapPost("", async (
            SparePartRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var now = ScadaTime.NowMs();
            var part = new SparePart
            {
                Code = request.Code?.Trim() ?? "", Name = request.Name?.Trim() ?? "", Specification = request.Specification?.Trim() ?? "",
                Unit = request.Unit?.Trim() ?? "", Stock = request.Stock, MinStock = request.MinStock, Location = request.Location?.Trim() ?? "",
                Supplier = request.Supplier?.Trim() ?? "", UnitPrice = request.UnitPrice, Description = request.Description?.Trim() ?? "",
                Enabled = request.Enabled, CreatedTs = now, UpdatedTs = now
            };
            var error = AssetValidation.SparePart(part);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.SpareParts.AnyAsync(x => x.Code == part.Code, ct)) return EndpointHelpers.BadRequest("备件编号已存在");
            db.SpareParts.Add(part);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("备件编号已存在"); }
            runtime.OperationLogs.Add(Audit.Change(http, $"创建备件：{part.Name}", "SparePart", part.Id, null, part));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Created($"/api/spare-parts/{part.Id}", part);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapPut("/{id:int}", async (
            int id, SparePartRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var part = await db.SpareParts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (part is null) return Results.NotFound();
            var before = EndpointHelpers.JsonCopy(part);
            part.Code = request.Code?.Trim() ?? ""; part.Name = request.Name?.Trim() ?? ""; part.Specification = request.Specification?.Trim() ?? "";
            part.Unit = request.Unit?.Trim() ?? ""; part.Stock = request.Stock; part.MinStock = request.MinStock; part.Location = request.Location?.Trim() ?? "";
            part.Supplier = request.Supplier?.Trim() ?? ""; part.UnitPrice = request.UnitPrice; part.Description = request.Description?.Trim() ?? "";
            part.Enabled = request.Enabled; part.UpdatedTs = ScadaTime.NowMs();
            var error = AssetValidation.SparePart(part);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            if (await db.SpareParts.AnyAsync(x => x.Id != id && x.Code == part.Code, ct)) return EndpointHelpers.BadRequest("备件编号已存在");
            runtime.OperationLogs.Add(Audit.Change(http, $"修改备件：{part.Name}", "SparePart", part.Id, before, part));
            try { await db.SaveChangesAsync(ct); await runtime.SaveChangesAsync(ct); }
            catch (DbUpdateException) { return EndpointHelpers.BadRequest("备件编号已存在"); }
            return Results.Ok(part);
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapDelete("/{id:int}", async (int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var part = await db.SpareParts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (part is null) return Results.NotFound();
            if (await db.SparePartMovements.AnyAsync(x => x.PartId == id, ct)) return EndpointHelpers.BadRequest("备件已有出入库流水，请停用而不是删除");
            db.SpareParts.Remove(part);
            runtime.OperationLogs.Add(Audit.Change(http, $"删除备件：{part.Name}", "SparePart", part.Id, part, null));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(AccessPolicies.Configure);

        group.MapGet("/{id:int}/movements", async (
            int id, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.SpareParts.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var rows = await db.SparePartMovements.AsNoTracking().Where(x => x.PartId == id).OrderByDescending(x => x.Ts).Take(500).ToListAsync(ct);
            return Results.Ok(rows);
        });

        group.MapPost("/{id:int}/movements", async (
            int id, SparePartMovementRequest request, HttpContext http, IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, CancellationToken ct) =>
        {
            var note = request.Note?.Trim() ?? "";
            var error = AssetValidation.Movement(request.Type, request.Quantity, note);
            if (error is not null) return EndpointHelpers.BadRequest(error);
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var runtime = await runtimeFactory.CreateDbContextAsync(ct);
            var part = await db.SpareParts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (part is null || !part.Enabled) return EndpointHelpers.BadRequest("备件不存在或已停用");
            if (request.WorkOrderId is int orderId && !await db.MaintenanceWorkOrders.AnyAsync(x => x.Id == orderId, ct))
                return EndpointHelpers.BadRequest("关联维护工单不存在");
            if (request.Type == SparePartMovementType.Out && part.Stock < request.Quantity)
                return EndpointHelpers.BadRequest($"备件库存不足，当前库存 {part.Stock:0.###} {part.Unit}".TrimEnd());
            var before = part.Stock;
            part.Stock = request.Type switch
            {
                SparePartMovementType.In => part.Stock + request.Quantity,
                SparePartMovementType.Out => part.Stock - request.Quantity,
                _ => request.Quantity
            };
            part.UpdatedTs = ScadaTime.NowMs();
            db.SparePartMovements.Add(new SparePartMovement
            {
                PartId = part.Id, Type = request.Type, Quantity = request.Quantity, BalanceAfter = part.Stock,
                WorkOrderId = request.WorkOrderId, Operator = http.User.GetUserName(), Note = note, Ts = part.UpdatedTs
            });
            runtime.OperationLogs.Add(Audit.Change(http, $"备件出入库：{part.Name} {request.Type}", "SparePart", part.Id, new { stock = before }, new { stock = part.Stock, request.Quantity }));
            await db.SaveChangesAsync(ct);
            await runtime.SaveChangesAsync(ct);
            return Results.Ok(part);
        }).RequireAuthorization(AccessPolicies.Control);
    }

    private static async Task<(int AreaId, string? Error)> ResolveWorkOrderAreaAsync(
        ConfigDbContext db, int? areaId, int? assetId, int? deviceId, CancellationToken ct)
    {
        if (deviceId is int id)
        {
            var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (device is null) return (0, "关联采集设备不存在");
            return (areaId ?? device.AreaId, null);
        }
        if (assetId is int assetKey)
        {
            var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(x => x.Id == assetKey, ct);
            if (asset is null) return (0, "关联设备资产不存在");
            return (areaId ?? asset.AreaId, null);
        }
        return (areaId ?? Area.DefaultId, null);
    }

    private static async Task SyncAssetStatusAsync(ConfigDbContext db, int? assetId, CancellationToken ct)
    {
        if (assetId is not int id) return;
        var asset = await db.Assets.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (asset is null || asset.Status == AssetStatus.Retired) return;
        var active = await db.MaintenanceWorkOrders.AnyAsync(x => x.AssetId == id && x.Status == WorkOrderStatus.InProgress, ct);
        if (active) asset.Status = AssetStatus.Maintenance;
        else if (asset.Status == AssetStatus.Maintenance) asset.Status = AssetStatus.InService;
        asset.UpdatedTs = ScadaTime.NowMs();
    }
}
