namespace Scada.Server.Ha;

// 主备配置（节 "Ha"）。两节点使用相同的 SharedKey 与 Jwt:Key；NodeId 各不相同。
public sealed class HaOptions
{
    public bool Enabled { get; set; }
    public string NodeId { get; set; } = "";
    // 对端节点的地址（节点间心跳、复制、交接用），如 http://10.0.0.12:5080/
    public string PeerUrl { get; set; } = "";
    // 本节点对外地址：备机拒绝网关和写请求时告诉对方去哪里。
    public string SelfUrl { get; set; } = "";
    public string SharedKey { get; set; } = "";
    // 仲裁目录（第三台机器上的共享目录）。配置后才允许自动切换；未配置时只支持手动/强制切换。
    public string? WitnessPath { get; set; }
    public bool AutoFailover { get; set; } = true;
    // 两节点同时启动时优先成为主机的一方。
    public bool PreferPrimary { get; set; }
    public int HeartbeatMs { get; set; } = 1000;
    public int LeaseMs { get; set; } = 6000;
    public int PeerTimeoutMs { get; set; } = 2000;
    // 两节点时钟允许的最大偏差（需 NTP 对时）。接管方在租约到期后再多等这么久。
    public int ClockSkewMs { get; set; } = 1000;
    public int ConfigSyncMs { get; set; } = 5000;
    public int HistorySyncMs { get; set; } = 2000;
    public int HistoryBatchRows { get; set; } = 5000;
    // 本节点私有状态目录（不会被复制覆盖），默认为配置库所在目录下的 ha。
    public string? StateDir { get; set; }

    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(NodeId) || NodeId.Length > 40 || NodeId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('-' or '_')))
            throw new InvalidOperationException("Ha:NodeId 无效（1～40 位字母、数字、- 或 _）");
        if (!Uri.TryCreate(PeerUrl, UriKind.Absolute, out var peer) || peer.Scheme is not ("http" or "https")) throw new InvalidOperationException("Ha:PeerUrl 无效");
        if (string.IsNullOrEmpty(SharedKey) || SharedKey.Length < 32) throw new InvalidOperationException("Ha:SharedKey 至少 32 个字符");
        if (HeartbeatMs is < 100 or > 10000 || LeaseMs < HeartbeatMs * 3 || LeaseMs > 120000 || PeerTimeoutMs is < 100 or > 30000 || ClockSkewMs is < 0 or > 30000
            || ConfigSyncMs is < 500 or > 3600000 || HistorySyncMs is < 200 or > 3600000 || HistoryBatchRows is < 100 or > 100000)
            throw new InvalidOperationException("Ha 时间参数无效：心跳 100～10000 毫秒，租约至少为心跳的 3 倍且不超过 120 秒");
    }
}

public sealed class HaException(string message, int status = 409) : Exception(message) { public int Status { get; } = status; }

public sealed record HaLease(string Holder, long Term, long ExpiresTs, string? HandoverTo = null);

// 仲裁：在两节点之外的第三方保存租约，比较并交换保证同一时刻至多一个持有者。可替换为其他实现（如数据库、HTTP 仲裁服务）。
public interface IHaWitness
{
    string Name { get; }
    Task<HaLease?> ReadAsync(CancellationToken ct);
    // 在独占锁内读取当前租约、计算新值并写回；返回写回后的租约（change 返回原对象或 null 表示不修改）。
    Task<HaLease?> UpdateAsync(Func<HaLease?, HaLease?> change, CancellationToken ct);
}

public sealed class FileHaWitness(string directory) : IHaWitness
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    public string Name => $"目录 {directory}";
    private string LeasePath => Path.Combine(directory, "lease.json");

    public Task<HaLease?> ReadAsync(CancellationToken ct) => UpdateAsync(x => x, ct);

    public async Task<HaLease?> UpdateAsync(Func<HaLease?, HaLease?> change, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        FileStream? gate = null;
        for (var attempt = 0; gate is null; attempt++)
        {
            try { gate = new FileStream(Path.Combine(directory, "lease.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(10, ct); }
        }
        using (gate)
        {
            HaLease? current = null;
            if (File.Exists(LeasePath))
            {
                try { current = System.Text.Json.JsonSerializer.Deserialize<HaLease>(await File.ReadAllTextAsync(LeasePath, ct), Json); }
                catch (System.Text.Json.JsonException) { current = null; } // 损坏视为无租约（写入是原子替换，正常不会出现）
            }
            var next = change(current);
            if (next is null || ReferenceEquals(next, current) || next == current) return current;
            var temp = LeasePath + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(stream, next, Json, ct);
                stream.Flush(true);
            }
            File.Move(temp, LeasePath, true);
            return next;
        }
    }
}
