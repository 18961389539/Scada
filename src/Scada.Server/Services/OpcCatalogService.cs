using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Opc.Ua;
using Scada.Collector.Opc;
using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Models;
using Scada.Data;
using ProtocolType = Scada.Core.Enums.ProtocolType;

namespace Scada.Server.Services;

public sealed record OpcBrowseRequest(string NodeId = "i=85", int Limit = 100, string? Cursor = null);
public sealed record OpcBrowseResponse(IReadOnlyList<OpcNodeInfo> Nodes, string? Cursor);
public sealed record OpcImportItem(string NodeId, string Name, bool Writable = false);
public sealed record OpcImportRequest(IReadOnlyList<OpcImportItem> Items);
public sealed record OpcImportRow(string NodeId, string Name, TagDataType? DataType, bool Writable, string? Error);
public sealed record OpcImportPreview(string Id, IReadOnlyList<OpcImportRow> Rows, IReadOnlyList<string> Errors, bool CanApply, DateTime ExpiresAt);

public sealed class OpcCatalogService(IDbContextFactory<ConfigDbContext> factory, OpcUaRuntime runtime) : BackgroundService
{
    private sealed class CursorState(int owner, int deviceId, string fingerprint, string nodeId, OpcCatalogSession session)
    {
        public int Owner = owner; public int DeviceId = deviceId; public string Fingerprint = fingerprint; public string NodeId = nodeId;
        public OpcCatalogSession Session = session; public ByteString Continuation; public DateTime Expires = DateTime.UtcNow.AddMinutes(5);
        public SemaphoreSlim Gate = new(1, 1);
    }
    private sealed record PreviewState(int Owner, int DeviceId, string Fingerprint, OpcImportPreview Preview, IReadOnlyList<OpcNodeInfo> Metadata);
    private readonly ConcurrentDictionary<string, CursorState> _cursors = new();
    private readonly ConcurrentDictionary<string, PreviewState> _previews = new();

    public async Task<OpcBrowseResponse> BrowseAsync(int owner, int deviceId, OpcBrowseRequest request, CancellationToken ct)
    {
        if (request.Limit is < 1 or > 200 || !OpcNodeAddress.IsValid(request.NodeId)) throw new ArgumentException("浏览节点或分页大小无效（1～200）");
        var device = await DeviceAsync(deviceId, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(device.TimeoutMs);
        var key = request.Cursor;
        CursorState cursor;
        if (key is null)
        {
            if (_cursors.Count >= 64 || _cursors.Values.Count(x => x.Owner == owner) >= 8) throw new InvalidOperationException("浏览会话已达上限，请关闭旧浏览或等待过期");
            var session = new OpcCatalogSession(runtime, await runtime.ConnectAsync(device, timeout.Token));
            key = Guid.NewGuid().ToString("N"); cursor = new(owner, deviceId, Fingerprint(device), request.NodeId, session);
            _cursors[key] = cursor;
        }
        else if (!_cursors.TryGetValue(key, out cursor!) || cursor.Owner != owner || cursor.DeviceId != deviceId || cursor.NodeId != request.NodeId || cursor.Expires < DateTime.UtcNow)
            throw new KeyNotFoundException("浏览游标已过期或不属于当前用户及节点");
        await cursor.Gate.WaitAsync(ct);
        try
        {
            if (cursor.Fingerprint != Fingerprint(device)) throw new InvalidOperationException("设备连接配置已变化，请重新浏览");
            var page = await cursor.Session.BrowseAsync(request.NodeId, request.Limit, cursor.Continuation, timeout.Token);
            cursor.Continuation = page.Continuation; cursor.Expires = DateTime.UtcNow.AddMinutes(5);
            if (page.Continuation.IsEmpty || page.Continuation.IsNull) { await CloseCursorAsync(key); key = null; }
            return new(page.Nodes, key);
        }
        catch { if (key is not null) await CloseCursorAsync(key); throw; }
        finally { cursor.Gate.Release(); }
    }

    public async Task CloseAsync(int owner, int deviceId, string key)
    { if (_cursors.TryGetValue(key, out var cursor) && cursor.Owner == owner && cursor.DeviceId == deviceId) await CloseCursorAsync(key); }
    public async Task InvalidateAsync()
    { foreach (var key in _cursors.Keys) await CloseCursorAsync(key); _previews.Clear(); }

    public async Task<OpcImportPreview> PreviewAsync(int owner, int deviceId, OpcImportRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Count is < 1 or > 500 || request.Items.Any(x => x is null || !OpcNodeAddress.IsValid(x.NodeId)))
            throw new ArgumentException("每次须选择 1～500 个有效变量");
        if (_previews.Count >= 128) throw new InvalidOperationException("导入预览已达上限，请等待旧预览过期");
        var device = await DeviceAsync(deviceId, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(device.TimeoutMs);
        await using var session = new OpcCatalogSession(runtime, await runtime.ConnectAsync(device, timeout.Token));
        var metadata = await session.MetadataAsync(request.Items.Select(x => x.NodeId).ToList(), timeout.Token);
        await using var db = await factory.CreateDbContextAsync(ct);
        var names = (await db.Tags.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nodes = new HashSet<string>(); var rows = new List<OpcImportRow>(); var errors = new List<string>();
        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i]; var info = metadata[i]; var name = item.Name?.Trim() ?? ""; var error = info.Error;
            if (name.Length is < 1 or > 100) error = "测点名称须为 1～100 个字符";
            else if (!names.Add(name)) error = "设备下测点名称已存在或本批次重复";
            else if (!nodes.Add(info.NodeId)) error = "同一变量被重复选择";
            else if (item.Writable && !info.Writable) error = "服务器未授予当前身份写入权限";
            if (error is not null) errors.Add($"{name}：{error}");
            rows.Add(new(info.NodeId, name, info.DataType, item.Writable, error));
        }
        var id = Guid.NewGuid().ToString("N"); var preview = new OpcImportPreview(id, rows, errors, errors.Count == 0, DateTime.UtcNow.AddMinutes(10));
        _previews[id] = new(owner, deviceId, Fingerprint(device), preview, metadata); return preview;
    }

    public async Task<IReadOnlyList<Tag>> ApplyAsync(int owner, int deviceId, string previewId, string userName, CancellationToken ct)
    {
        if (!_previews.TryGetValue(previewId, out var saved) || saved.Owner != owner || saved.DeviceId != deviceId || saved.Preview.ExpiresAt < DateTime.UtcNow)
            throw new KeyNotFoundException("导入预览不存在或已过期，请重新预览");
        if (!saved.Preview.CanApply) throw new ArgumentException("导入预览包含错误");
        await using var db = await factory.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == deviceId, ct) ?? throw new KeyNotFoundException("设备不存在");
        if (saved.Fingerprint != Fingerprint(device)) throw new InvalidOperationException("设备配置自预览后已变化，请重新预览");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(device.TimeoutMs);
        await using var session = new OpcCatalogSession(runtime, await runtime.ConnectAsync(device, timeout.Token));
        var metadata = await session.MetadataAsync(saved.Preview.Rows.Select(x => x.NodeId).ToList(), timeout.Token);
        if (ObjectTemplateJson.Write(saved.Metadata) != ObjectTemplateJson.Write(metadata)) throw new InvalidOperationException("服务器变量类型或访问权限自预览后已变化，请重新预览");
        var names = (await db.Tags.AsNoTracking().Where(x => x.DeviceId == deviceId).Select(x => x.Name).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (saved.Preview.Rows.Any(x => names.Contains(x.Name))) throw new InvalidOperationException("测点名称自预览后已被使用，请重新预览");
        var tags = saved.Preview.Rows.Select(x => new Tag { DeviceId = deviceId, Name = x.Name, NodeId = x.NodeId, Area = TagArea.OpcNode, DataType = x.DataType!.Value, Writable = x.Writable }).ToList();
        db.Tags.AddRange(tags);
        db.OperationLogs.Add(new OperationLog { UserId = owner, UserName = userName, Ts = ScadaTime.NowMs(), Action = $"导入 OPC UA 变量：设备 #{deviceId}，{tags.Count} 个测点", Result = "成功" });
        await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); _previews.TryRemove(previewId, out _); return tags;
    }

    private async Task<Device> DeviceAsync(int id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("设备不存在");
        if (device.Protocol != ProtocolType.OpcUa) throw new ArgumentException("此设备不是 OPC UA 协议"); return device;
    }
    private static string Fingerprint(Device device) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ObjectTemplateJson.Write(device) + device.OpcPasswordProtected)));
    private async Task CloseCursorAsync(string key) { if (_cursors.TryRemove(key, out var cursor)) await cursor.Session.DisposeAsync(); }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var (key, state) in _cursors) if (state.Expires < DateTime.UtcNow) await CloseCursorAsync(key);
                foreach (var (key, state) in _previews) if (state.Preview.ExpiresAt < DateTime.UtcNow) _previews.TryRemove(key, out _);
            }
        }
        catch (OperationCanceledException) { }
        finally { foreach (var key in _cursors.Keys) await CloseCursorAsync(key); _previews.Clear(); }
    }
}
