using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Opc.Ua;
using Scada.Collector.Opc;
using Scada.Core.Opc;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Core.Protocols;
using Scada.Data;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Server.Services;

public sealed record OpcImportItem(string NodeId, string Name, bool Writable = false);
public sealed record OpcImportRequest(IReadOnlyList<OpcImportItem> Items);
public sealed record OpcImportRow(string NodeId, string Name, TagDataType? DataType, bool Writable, string? Error);
public sealed record OpcImportPreview(string Id, IReadOnlyList<OpcImportRow> Rows, IReadOnlyList<string> Errors, bool CanApply, DateTime ExpiresAt);

public sealed class OpcCatalogService(IDbContextFactory<ConfigDbContext> factory, IDbContextFactory<RuntimeDbContext> runtimeFactory, OpcUaRuntime runtime, GatewayCenterService gateways, Microsoft.Extensions.Options.IOptions<Scada.Core.Options.ScadaSettings> settings) : BackgroundService
{
    private sealed record PreviewState(int Owner, int DeviceId, string Fingerprint, OpcImportPreview Preview, IReadOnlyList<OpcNodeInfo> Metadata, int? GatewayId);
    private readonly OpcBrowseRegistry _local = new(runtime);
    private readonly ConcurrentDictionary<string, PreviewState> _previews = new();

    private void EnsureLocalEngineering(Device device)
    {
        if (device.GatewayId is null && !settings.Value.LocalCollectionEnabled)
            throw new InvalidOperationException(Scada.Core.LocalCollection.DisabledMessage);
    }


    public async Task<OpcBrowseResponse> BrowseAsync(int owner, int deviceId, OpcBrowseRequest request, CancellationToken ct)
    {
        if (request.Limit is < 1 or > 200 || !OpcNodeAddress.IsValid(request.NodeId)) throw new ArgumentException("浏览节点或分页大小无效（1～200）");
        var device = await DeviceAsync(deviceId, ct);
        if (device.GatewayId is not null) return await gateways.EngineerAsync<OpcBrowseResponse>(deviceId, GatewayEngineeringOperation.OpcBrowse, request, owner, ct);
        EnsureLocalEngineering(device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(device.TimeoutMs);
        return await _local.BrowseAsync(owner, device, Fingerprint(device), request, timeout.Token);
    }

    public async Task CloseAsync(int owner, int deviceId, string key, CancellationToken ct = default)
    {
        var device = await DeviceAsync(deviceId, ct);
        if (device.GatewayId is not null) await gateways.EngineerAsync<JsonElement>(deviceId, GatewayEngineeringOperation.OpcClose, new { cursor = key }, owner, ct);
        else
        {
            EnsureLocalEngineering(device);
            await _local.CloseAsync(owner, deviceId, key, ct);
        }
    }
    public async Task InvalidateAsync(int? gatewayId = null)
    {
        if (gatewayId is null) await _local.InvalidateAsync();
        foreach (var (key, preview) in _previews) if (preview.GatewayId == gatewayId) _previews.TryRemove(key, out _);
    }

    public async Task<OpcImportPreview> PreviewAsync(int owner, int deviceId, OpcImportRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count is < 1 or > 500 || request.Items.Any(x => x is null || !OpcNodeAddress.IsValid(x.NodeId)))
            throw new ArgumentException("每次须选择 1～500 个有效变量");
        if (_previews.Count >= 128) throw new InvalidOperationException("导入预览已达上限，请等待旧预览过期");
        var device = await DeviceAsync(deviceId, ct);
        var metadata = await MetadataAsync(owner, device, request.Items.Select(x => x.NodeId).ToArray(), ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var existingTags = await db.Tags.AsNoTracking().Where(x => x.DeviceId == deviceId).ToListAsync(ct);
        var names = existingTags.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occupiedNodes = existingTags.Where(x => !string.IsNullOrEmpty(x.NodeId))
            .GroupBy(x => x.NodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var nodes = new HashSet<string>(StringComparer.Ordinal); var rows = new List<OpcImportRow>(); var errors = new List<string>();
        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i]; var info = metadata[i]; var name = item.Name?.Trim() ?? ""; var error = info.Error;
            if (name.Length is < 1 or > 100) error = "测点名称须为 1～100 个字符";
            else if (!names.Add(name)) error = "设备下测点名称已存在或本批次重复";
            else if (!nodes.Add(info.NodeId)) error = "同一变量被重复选择";
            else if (occupiedNodes.TryGetValue(info.NodeId, out var existingName)) error = $"设备上已存在相同 OPC 变量（测点「{existingName}」）";
            else if (item.Writable && !info.Writable) error = "服务器未授予当前身份写入权限";
            if (error is not null) errors.Add($"{name}：{error}");
            rows.Add(new(info.NodeId, name, info.DataType, item.Writable, error));
        }
        var planned = rows.Where(x => x.Error is null && x.DataType is not null)
            .Select(x => ImportTag(deviceId, x)).ToList();
        foreach (var message in AddressOverlapErrors(planned, existingTags, device))
        {
            errors.Add(message);
            var overlapName = message.Split('：', 2)[0];
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Error is null && rows[i].Name == overlapName)
                    rows[i] = rows[i] with { Error = message[(overlapName.Length + 1)..] };
        }
        var id = Guid.NewGuid().ToString("N"); var preview = new OpcImportPreview(id, rows, errors, errors.Count == 0, DateTime.UtcNow.AddMinutes(10));
        _previews[id] = new(owner, deviceId, Fingerprint(device), preview, metadata, device.GatewayId); return preview;
    }

    public async Task<IReadOnlyList<Tag>> ApplyAsync(int owner, int deviceId, string previewId, string userName, CancellationToken ct)
    {
        if (!_previews.TryGetValue(previewId, out var saved) || saved.Owner != owner || saved.DeviceId != deviceId || saved.Preview.ExpiresAt < DateTime.UtcNow)
            throw new KeyNotFoundException("导入预览不存在或已过期，请重新预览");
        if (!saved.Preview.CanApply) throw new ArgumentException("导入预览包含错误");
        var before = await DeviceAsync(deviceId, ct);
        if (saved.Fingerprint != Fingerprint(before)) throw new InvalidOperationException("设备配置自预览后已变化，请重新预览");
        // Fetch remotely before the write transaction; remote RPC itself needs the configuration database.
        var metadata = await MetadataAsync(owner, before, saved.Preview.Rows.Select(x => x.NodeId).ToArray(), ct);
        await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await using var runtimeDb = await runtimeFactory.CreateDbContextAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new KeyNotFoundException("设备不存在");
        if (!await AccessModelProvider.UserHasAsync(db, owner, Scada.Core.Security.Permission.Configure, device.AreaId, ct)) throw new GatewayRequestException(403, "导入权限已撤销");
        if (saved.Fingerprint != Fingerprint(device)) throw new InvalidOperationException("设备配置自预览后已变化，请重新预览");
        if (ObjectTemplateJson.Write(saved.Metadata) != ObjectTemplateJson.Write(metadata)) throw new InvalidOperationException("服务器变量类型或访问权限自预览后已变化，请重新预览");
        var existingTags = await db.Tags.AsNoTracking().Where(x => x.DeviceId == deviceId).ToListAsync(ct);
        var names = existingTags.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (saved.Preview.Rows.Any(x => names.Contains(x.Name))) throw new InvalidOperationException("测点名称自预览后已被使用，请重新预览");
        var tags = saved.Preview.Rows.Select(x => ImportTag(deviceId, x)).ToList();
        if (ImportConflicts(tags, existingTags, device) is { Count: > 0 } conflicts)
            throw new InvalidOperationException("测点地址或 OPC 变量自预览后已冲突，请重新预览：" + string.Join("；", conflicts));
        // 一次导入可能上千个变量：保留下面这条汇总审计，抑制逐条。
        using var importAudit = Scada.Core.Security.AuditContext.Suppress();
        db.Tags.AddRange(tags);
        runtimeDb.OperationLogs.Add(new OperationLog { UserId = owner, UserName = userName, Ts = ScadaTime.NowMs(), Action = $"导入 OPC UA 变量：设备 #{deviceId}，{tags.Count} 个测点", Result = "成功" });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); await runtimeDb.SaveChangesAsync(ct); _previews.TryRemove(previewId, out _); return tags;
    }

    public static Tag ImportTag(int deviceId, OpcImportRow row) => new()
    {
        DeviceId = deviceId, Name = row.Name, NodeId = row.NodeId, Area = TagArea.OpcNode,
        DataType = row.DataType!.Value, Writable = row.Writable, TextLength = row.DataType == TagDataType.String ? 32 : 0
    };

    public static IReadOnlyList<string> ImportConflicts(IReadOnlyList<Tag> incoming, IReadOnlyList<Tag> existing, Device device)
    {
        var errors = new List<string>();
        var occupied = existing.Where(x => !string.IsNullOrEmpty(x.NodeId))
            .GroupBy(x => x.NodeId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var batch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in incoming)
        {
            if (string.IsNullOrEmpty(tag.NodeId)) continue;
            if (!batch.Add(tag.NodeId)) errors.Add($"{tag.Name}：同一变量被重复选择");
            else if (occupied.TryGetValue(tag.NodeId, out var name)) errors.Add($"{tag.Name}：设备上已存在相同 OPC 变量（测点「{name}」）");
        }
        errors.AddRange(AddressOverlapErrors(incoming, existing, device));
        return errors;
    }

    public static IReadOnlyList<string> AddressOverlapErrors(IEnumerable<Tag> incoming, IEnumerable<Tag> existing, Device device) =>
        AddressOverlap.ConflictsAmong(incoming.Concat(existing), device)
            .Select(o => $"{o.TagName}：与测点「{o.OtherTagName}」{(o.OtherTagId > 0 ? $"#{o.OtherTagId}" : "")}冲突：{o.Reason}")
            .ToList();

    private async Task<Device> DeviceAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("设备不存在");
        if (device.Protocol != ProtocolType.OpcUa) throw new ArgumentException("此设备不是 OPC UA 协议");
        return device;
    }
    private async Task<IReadOnlyList<OpcNodeInfo>> MetadataAsync(int owner, Device device, IReadOnlyList<string> nodes, CancellationToken ct)
    {
        if (device.GatewayId is not null) return await gateways.EngineerAsync<IReadOnlyList<OpcNodeInfo>>(device.Id, GatewayEngineeringOperation.OpcMetadata, nodes, owner, ct);
        EnsureLocalEngineering(device);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(device.TimeoutMs);
        await using var session = new OpcCatalogSession(runtime, await runtime.ConnectAsync(device, timeout.Token));
        return await session.MetadataAsync(nodes, timeout.Token);
    }
    private static string Fingerprint(Device device) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ObjectTemplateJson.Write(device) + device.OpcPasswordProtected)));
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _local.PruneAsync();
                foreach (var (key, state) in _previews) if (state.Preview.ExpiresAt < DateTime.UtcNow) _previews.TryRemove(key, out _);
            }
        }
        catch (OperationCanceledException) { }
        finally { await _local.DisposeAsync(); _previews.Clear(); }
    }
}
