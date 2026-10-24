using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Scada.Core.Models;

namespace Scada.Gateway.Hosting;

public sealed record GatewaySavedState(int Format, int GatewayId, string Server, string KeyStamp, GatewayConfiguration? Configuration, bool Confirmed, bool Blocked);

public sealed class GatewayConfigurationCache(GatewayAgentStore store, IDataProtectionProvider protection, GatewayAgentOptions options)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Scada.Gateway.Configuration.v1");
    public string KeyStamp { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Token)));
    public async Task<GatewaySavedState> LoadAsync(CancellationToken ct)
    {
        var cipher = await store.LoadProtectedConfigurationAsync(ct);
        if (cipher is null)
        {
            if ((await store.StatusAsync(ct)).PendingBatches != 0) throw new InvalidDataException("已有网关队列缺少安装归属配置，保留数据并拒绝自动绑定");
            var initial = new GatewaySavedState(1, options.GatewayId, options.Server.AbsoluteUri, KeyStamp, null, false, false);
            await SaveAsync(initial, ct); return initial;
        }
        var state = JsonSerializer.Deserialize<GatewaySavedState>(_protector.Unprotect(cipher), GatewayProtocol.Json) ?? throw new InvalidDataException("离线配置为空");
        if (state.Format != 1 || state.GatewayId != options.GatewayId || state.Server != options.Server.AbsoluteUri)
            throw new InvalidDataException("此数据目录属于其他中心或网关，未修改原队列");
        return state;
    }
    public async Task SaveAsync(GatewaySavedState state, CancellationToken ct)
    {
        try { await store.SaveProtectedConfigurationAsync(_protector.Protect(JsonSerializer.Serialize(state, GatewayProtocol.Json)), ct); }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        { throw new InvalidOperationException("无法持久保存网关授权及配置状态", ex); }
    }
}
