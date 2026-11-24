using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Options;
using Scada.Data;
using Scada.Core.Interfaces;
using Scada.Core.Models;
using Scada.Engine.History;
using Scada.Server.Services;

namespace Scada.Server.Ha;

public sealed record HaManifest(string NodeId, string Role, long Term, string Schema, string ConfigSha, long ConfigBytes, IReadOnlyList<HaHistoryCursor> History, string? RuntimeSha = null, long? RuntimeBytes = null, long RuntimeWatermark = 0, string? SecretsSha = null, long SecretsBytes = 0, HaSpoolAdvertisement? Spool = null, string? HistoryLossSha = null);
public sealed record HaSpoolAdvertisement(long Bytes, long ApproxCount, int Segments, long? From, long? To);

// 本节点私有状态：任期、已应用的配置摘要、历史复制游标。放在 StateDir，不在配置库里，因此不会被复制覆盖。
public sealed class HaNodeState
{
    public long Term { get; set; }
    public string? ConfigSha { get; set; }
    public string? RuntimeSha { get; set; }
    public System.Collections.Concurrent.ConcurrentDictionary<string, long> Cursors { get; set; } = new(StringComparer.Ordinal);
}

// 应用层复制。评估结论见 docs/high-availability.md：
// - 配置库：快照传送。主机用 SQLite 在线备份生成一致快照并按 SHA256 去重，备机拉取后用在线备份写回本地库，再执行迁移并重载。
// - 历史库：通过 IHistoryBackend 增量拉取（SQLite 后端内部按文件与 rowid 实现；协议字段不变）。
// 不采用 Litestream：它不支持 Windows，且 WAL 帧级复制要求接管页面检查点，与现有按月分库、运行中迁移的结构冲突。
public sealed class HaReplication(IOptions<ScadaSettings> settings, IDbContextFactory<ConfigDbContext> factory, IHistoryBackend history,
    IServiceProvider services, ILogger<HaReplication> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private readonly ScadaSettings _settings = settings.Value;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1), _applyGate = new(1, 1);
    private (string Key, string Sha, long Bytes)? _snapshot, _runtimeSnapshot;

    public string ConfigPath => Path.GetFullPath(_settings.ConfigDbPath);
    public string RuntimePath => DataServiceCollectionExtensions.ResolveRuntimePath(_settings);
    public string HistoryRoot => Path.GetFullPath(_settings.HistoryRoot);
    public string? LastError { get; private set; }
    public long LastConfigSyncTs { get; private set; }
    public long LastHistorySyncTs { get; private set; }
    public long HistoryRowsBehind { get; private set; }
    public int HistoryLagStreak => HistoryLag.Streak;
    public HaHistoryLagMonitor HistoryLag { get; } = new();
    public long ConfigApplied { get; private set; }
    public long HistoryRowsApplied { get; private set; }
    public long LastRuntimeSyncTs { get; private set; }
    public long RuntimeRowsApplied { get; private set; }
    public long RuntimeRowsBehind { get; private set; }
    public int RuntimeLagStreak => RuntimeLag.Streak;
    public HaHistoryLagMonitor RuntimeLag { get; } = new("运行库复制");
    public HaSpoolAdvertisement? PeerSpool { get; private set; }
    public bool HistoryArtifactsImported { get; private set; }
    public const long SpoolUnreplicatedWarnBytes = 1_048_576;

    public string KeysDir => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "keys");
    public string? OpcPkiRoot => string.IsNullOrWhiteSpace(_settings.OpcPkiRoot)
        ? Path.Combine(Path.GetDirectoryName(ConfigPath)!, "opc-pki")
        : Path.GetFullPath(_settings.OpcPkiRoot);

    // ---------- 主机侧 ----------

    public async Task<string> SchemaAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault() ?? "";
    }

    public async Task<(string Path, string Sha, long Bytes)> ConfigSnapshotAsync(string stateDir, CancellationToken ct)
    {
        await _snapshotGate.WaitAsync(ct);
        try
        {
            var target = Path.Combine(stateDir, "outgoing-config.db");
            var key = Stamp(ConfigPath) + "|" + Stamp(ConfigPath + "-wal");
            if (_snapshot is { } s && s.Key == key && File.Exists(target)) return (target, s.Sha, s.Bytes);
            Directory.CreateDirectory(stateDir);
            var temp = target + ".tmp";
            File.Delete(temp);
            using (var from = new SqliteConnection($"Data Source={ConfigPath};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var cmd = to.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=DELETE"; cmd.ExecuteNonQuery();
            }
            File.Move(temp, target, true);
            var sha = Hash(target); var bytes = new FileInfo(target).Length;
            _snapshot = (key, sha, bytes);
            return (target, sha, bytes);
        }
        finally { _snapshotGate.Release(); }
    }

    public async Task<(string Path, string Sha, long Bytes)?> RuntimeSnapshotAsync(string stateDir, CancellationToken ct)
    {
        if (!File.Exists(RuntimePath)) return null;
        await _snapshotGate.WaitAsync(ct);
        try
        {
            var target = Path.Combine(stateDir, "outgoing-runtime.db");
            var key = Stamp(RuntimePath) + "|" + Stamp(RuntimePath + "-wal");
            if (_runtimeSnapshot is { } s && s.Key == key && File.Exists(target)) return (target, s.Sha, s.Bytes);
            Directory.CreateDirectory(stateDir);
            var temp = target + ".tmp";
            File.Delete(temp);
            using (var from = new SqliteConnection($"Data Source={RuntimePath};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={temp};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var cmd = to.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode=DELETE"; cmd.ExecuteNonQuery();
            }
            File.Move(temp, target, true);
            var sha = Hash(target); var bytes = new FileInfo(target).Length;
            _runtimeSnapshot = (key, sha, bytes);
            return (target, sha, bytes);
        }
        finally { _snapshotGate.Release(); }
    }

    public IReadOnlyList<HaHistoryCursor> HistoryCursors() => history.GetReplicaCursors();

    public HaHistoryRows ReadHistory(string file, string table, long after, int limit)
    {
        try { return history.ReadReplica(file, table, after, limit); }
        catch (HistoryReplicaException ex) { throw new HaException(ex.Message, ex.Status); }
    }

    public long RuntimeWatermark()
    {
        if (!File.Exists(RuntimePath)) return 0;
        using var connection = OpenRuntime();
        var watermark = HaRuntimeSync.Watermark(connection);
        // 变更日志只为落后的备机保留一段窗口；已应用的旧行可以丢掉。
        if (watermark > 200_000) HaRuntimeSync.Trim(connection, watermark - 200_000);
        return watermark;
    }

    public HaRuntimeChanges ReadRuntimeChanges(long after, int limit)
    {
        if (!File.Exists(RuntimePath)) return new HaRuntimeChanges(after, after, false, []);
        using var connection = OpenRuntime();
        return HaRuntimeSync.Read(connection, after, limit);
    }

    public void EnsureRuntimeChangeFeed()
    {
        if (!File.Exists(RuntimePath)) return;
        using var connection = OpenRuntime();
        HaRuntimeSync.EnsureChangeFeed(connection, rebuild: true);
    }

    private SqliteConnection OpenRuntime()
    {
        var connection = new SqliteConnection($"Data Source={RuntimePath};Pooling=False");
        connection.Open();
        return connection;
    }

    // ---------- 备机侧 ----------

    // 拉取并应用。force=true 用于交接前的最终同步：不论对端角色，追平全部历史。
    public async Task SyncAsync(HaPeerClient peer, HaNodeState state, Func<Task> save, bool config, bool historyDue, bool runtimeDue, bool force, CancellationToken ct)
    {
        await _applyGate.WaitAsync(ct);
        try
        {
            var manifest = await peer.ManifestAsync(ct);
            if (!force && manifest.Role != "Primary") return;
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                if (!db.Database.GetMigrations().Contains(manifest.Schema) && !string.IsNullOrEmpty(manifest.Schema))
                    throw new HaException($"对端数据结构版本较新（{manifest.Schema}），已暂停复制；请先升级本节点");
            }
            const string runtimeCursor = "runtime";
            var bootstrapped = state.Cursors.ContainsKey(runtimeCursor);
            PeerSpool = manifest.Spool;
            if (force) HistoryArtifactsImported = false;
            var runtimeCaughtUp = !runtimeDue && !force;
            // 全库快照仅用于强制切换或备机尚未引导。日常路径按变更游标增量拉取，避免 RuntimeSha 因高周转行每几秒都变而反复下载整个库。
            if (force || (runtimeDue && !bootstrapped))
            {
                // 首次引导或强制切换必须整库拉取：变更日志不会回放升级前已有的运行行。
                await ApplyRuntimeAsync(peer, manifest, ct);
                state.Cursors[runtimeCursor] = manifest.RuntimeWatermark;
                state.RuntimeSha = manifest.RuntimeSha;
                await save();
                RuntimeRowsBehind = 0;
                RuntimeLag.Observe(0);
                runtimeCaughtUp = true;
            }
            else if (runtimeDue && manifest.RuntimeWatermark > state.Cursors.GetValueOrDefault(runtimeCursor))
            {
                var at = state.Cursors.GetValueOrDefault(runtimeCursor);
                var deadline = Environment.TickCount64 + (force ? 20000 : 2000);
                IReadOnlyList<string> pendingTables = RuntimeLag.Tables;
                while (at < manifest.RuntimeWatermark && Environment.TickCount64 < deadline)
                {
                    var batch = await peer.RuntimeChangesAsync(at, ct);
                    if (batch.Gap)
                    {
                        // 主机裁剪掉的窗口已经越过游标：增量复制无法补齐，只能用整库快照重置基线。
                        logger.LogWarning("运行库变更窗口已越过游标（游标 {Cursor}，最早可用 {Oldest}），改为整库同步", at, batch.Oldest);
                        await ApplyRuntimeAsync(peer, manifest, ct);
                        state.Cursors[runtimeCursor] = manifest.RuntimeWatermark;
                        state.RuntimeSha = manifest.RuntimeSha;
                        await save();
                        at = manifest.RuntimeWatermark;
                        pendingTables = [];
                        break;
                    }
                    if (batch.Changes.Count == 0) { at = Math.Max(at, batch.Through); pendingTables = batch.PendingTables ?? pendingTables; break; }
                    ApplyRuntimeChanges(batch);
                    at = batch.Through;
                    RuntimeRowsApplied += batch.Changes.Count;
                    if (batch.PendingTables is { Count: > 0 }) pendingTables = batch.PendingTables;
                    state.Cursors[runtimeCursor] = at; await save();
                    if (!batch.More) break;
                }
                state.Cursors[runtimeCursor] = at; await save();
                var behind = Math.Max(0, manifest.RuntimeWatermark - at);
                RuntimeRowsBehind = behind;
                RuntimeLag.Observe(behind, pendingTables);
                runtimeCaughtUp = behind == 0;
            }
            else if (runtimeDue)
            {
                RuntimeRowsBehind = 0;
                RuntimeLag.Observe(0);
                runtimeCaughtUp = true;
            }
            if (runtimeCaughtUp) LastRuntimeSyncTs = Scada.Core.Models.ScadaTime.NowMs();
            if (config || force) await SyncSecretsAsync(peer, manifest, ct);
            if (force) await SyncHistoryArtifactsAsync(peer, manifest, ct);
            if ((config || force) && manifest.ConfigSha != state.ConfigSha)
            {
                await ApplyConfigAsync(peer, manifest, ct);
                state.ConfigSha = manifest.ConfigSha; await save();
            }
            if (config || force) LastConfigSyncTs = Scada.Core.Models.ScadaTime.NowMs();
            if (historyDue || force)
            {
                var deadline = Environment.TickCount64 + (force ? 20000 : 2000);
                long behind = 0;
                foreach (var cursor in manifest.History)
                {
                    var key = cursor.File + "|" + cursor.Table;
                    var at = state.Cursors.GetValueOrDefault(key);
                    if (cursor.MaxRowId < at) at = 0; // 对端重建了该文件（如被保留策略删除后重建）
                    while (at < cursor.MaxRowId && Environment.TickCount64 < deadline)
                    {
                        var batch = await peer.HistoryAsync(cursor.File, cursor.Table, at, ct);
                        if (batch.Rows.Count == 0) break;
                        ApplyHistory(batch);
                        at = long.Parse(batch.Rows[^1][0]!, System.Globalization.CultureInfo.InvariantCulture); HistoryRowsApplied += batch.Rows.Count;
                        state.Cursors[key] = at; await save();
                    }
                    behind += Math.Max(0, cursor.MaxRowId - at);
                }
                HistoryRowsBehind = behind;
                HistoryLag.Observe(behind);
                LastHistorySyncTs = Scada.Core.Models.ScadaTime.NowMs();
            }
            LastError = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = ex.Message;
            if (force) throw;
            logger.LogWarning("主备复制失败：{Message}", ex.Message);
        }
        finally { _applyGate.Release(); }
    }

    private async Task ApplyRuntimeAsync(HaPeerClient peer, HaManifest manifest, CancellationToken ct)
    {
        var incoming = Path.Combine(Path.GetDirectoryName(RuntimePath)!, ".ha-incoming-runtime.db");
        File.Delete(incoming);
        try
        {
            try { await peer.DownloadRuntimeAsync(incoming, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation("主机尚无运行库，跳过整库同步");
                return;
            }
            if (manifest.RuntimeSha is { Length: > 0 } expected)
            {
                var sha = Hash(incoming);
                if (!string.Equals(sha, expected, StringComparison.OrdinalIgnoreCase))
                    throw new HaException("运行库快照在传输中已更新，稍后重试");
            }
            ClearLocalPools();
            Directory.CreateDirectory(Path.GetDirectoryName(RuntimePath)!);
            using (var from = new SqliteConnection($"Data Source={incoming};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={RuntimePath};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var wal = to.CreateCommand(); wal.CommandText = "PRAGMA journal_mode=WAL"; wal.ExecuteNonQuery();
            }
            ClearLocalPools();
            await using var db = await services.GetRequiredService<IDbContextFactory<RuntimeDbContext>>().CreateDbContextAsync(ct);
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("已从主机同步运行库（{Bytes} 字节）", manifest.RuntimeBytes);
        }
        finally { try { File.Delete(incoming); } catch { } }
    }

    private async Task ApplyConfigAsync(HaPeerClient peer, HaManifest manifest, CancellationToken ct)
    {
        var incoming = Path.Combine(Path.GetDirectoryName(ConfigPath)!, ".ha-incoming-config.db");
        File.Delete(incoming);
        try
        {
            await peer.DownloadConfigAsync(incoming, ct);
            var sha = Hash(incoming);
            if (!string.Equals(sha, manifest.ConfigSha, StringComparison.OrdinalIgnoreCase))
            {
                // 下载期间主机又生成了新快照：下次再取，不应用不一致的文件。
                throw new HaException("配置快照在传输中已更新，稍后重试");
            }
            ClearLocalPools();
            using (var from = new SqliteConnection($"Data Source={incoming};Pooling=False"))
            using (var to = new SqliteConnection($"Data Source={ConfigPath};Pooling=False"))
            {
                from.Open(); to.Open(); from.BackupDatabase(to);
                using var wal = to.CreateCommand(); wal.CommandText = "PRAGMA journal_mode=WAL"; wal.ExecuteNonQuery();
            }
            ClearLocalPools();
            await using (var db = await factory.CreateDbContextAsync(ct)) await db.Database.MigrateAsync(ct); // 本节点版本更新时就地升级
            await services.GetRequiredService<ConfigReloader>().ReloadAsync(ct);
            ConfigApplied++;
            logger.LogInformation("已从主机同步配置库（{Bytes} 字节）", manifest.ConfigBytes);
        }
        finally { try { File.Delete(incoming); } catch { } }
    }

    private void ApplyHistory(HaHistoryRows batch)
    {
        try { history.ApplyReplica(batch); }
        catch (HistoryReplicaException ex) { throw new HaException(ex.Message, ex.Status); }
    }

    private void ApplyRuntimeChanges(HaRuntimeChanges batch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RuntimePath)!);
        using var connection = OpenRuntime();
        // 备机回放不该进入自己的变更流：先移除触发器再应用（提升为主机时按 rebuild 重建）。
        HaRuntimeSync.PrepareReplica(connection);
        HaRuntimeSync.Apply(connection, batch);
    }

    // 换库前后要丢掉池里的旧连接，但只能用「按库清」：ClearAllPools 是进程级的，
    // 会把同进程里其他宿主（测试夹具、托盘多站点）的在途连接一起关掉，
    // 表现为别处的 "attempt to write a readonly database" / "no such table"——与主备毫无关系却当场炸。
    private void ClearLocalPools()
    {
        Clear(DataServiceCollectionExtensions.ConfigConnectionString(_settings));
        Clear(DataServiceCollectionExtensions.RuntimeConnectionString(_settings));

        static void Clear(string connectionString)
        {
            using var probe = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(probe);
        }
    }


    public (string Path, string Sha, long Bytes)? SecretsSnapshot(string stateDir) =>
        HaSecretSync.Snapshot(stateDir, KeysDir, OpcPkiRoot);

    public HaSpoolAdvertisement? AdvertiseSpool()
    {
        var spool = services.GetService<HistorySpool>();
        if (spool is null) return null;
        var pending = spool.DescribePending();
        if (pending.Segments == 0 && pending.Bytes == 0 && pending.ApproxCount == 0) return null;
        return new HaSpoolAdvertisement(pending.Bytes, pending.ApproxCount, pending.Segments, pending.From, pending.To);
    }

    public string? HistoryLossSha()
    {
        var path = Path.Combine(HistoryRoot, HistoryLossLog.FileName);
        return File.Exists(path) ? Hash(path) : null;
    }

    public string HistoryLossPath() => Path.Combine(HistoryRoot, HistoryLossLog.FileName);

    private async Task SyncSecretsAsync(HaPeerClient peer, HaManifest manifest, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(manifest.SecretsSha)) return;
        var incoming = Path.Combine(Path.GetDirectoryName(ConfigPath)!, ".ha-incoming-secrets.zip");
        File.Delete(incoming);
        try
        {
            await peer.DownloadSecretsAsync(incoming, ct);
            var sha = Hash(incoming);
            if (!string.Equals(sha, manifest.SecretsSha, StringComparison.OrdinalIgnoreCase))
                throw new HaException("密钥环在传输中已更新，稍后重试");
            HaSecretSync.Apply(incoming, KeysDir, OpcPkiRoot);
            logger.LogInformation("已从主机同步 Data Protection 密钥环与 OPC PKI（{Bytes} 字节）", manifest.SecretsBytes);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("主机尚未提供密钥环快照，跳过");
        }
        finally { try { File.Delete(incoming); } catch { } }
    }

    private async Task SyncHistoryArtifactsAsync(HaPeerClient peer, HaManifest manifest, CancellationToken ct)
    {
        var spool = services.GetService<HistorySpool>();
        var incomingSpool = Path.Combine(HistoryRoot, ".ha-incoming-spool");
        try
        {
            if (Directory.Exists(incomingSpool)) Directory.Delete(incomingSpool, true);
            await peer.DownloadSpoolAsync(incomingSpool, ct);
            if (spool is not null && Directory.Exists(incomingSpool))
                await spool.MergeFromDirectoryAsync(incomingSpool, ct);
            var writer = services.GetService<HistoryWriterService>();
            if (writer is not null) await writer.RecoverSpoolAsync(ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogInformation("主机没有可复制的历史暂存");
        }
        finally { try { if (Directory.Exists(incomingSpool)) Directory.Delete(incomingSpool, true); } catch { } }

        var loss = services.GetService<HistoryLossLog>();
        var incomingLoss = Path.Combine(HistoryRoot, ".ha-incoming-history-loss.jsonl");
        try
        {
            File.Delete(incomingLoss);
            await peer.DownloadHistoryLossAsync(incomingLoss, ct);
            if (loss is not null && File.Exists(incomingLoss))
            {
                var merged = loss.MergeFromFile(incomingLoss);
                if (merged > 0) logger.LogInformation("已合并对端历史缺口日志 {Count} 条", merged);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // 对端没有缺口日志属于正常情况。
        }
        finally { try { File.Delete(incomingLoss); } catch { } }
        HistoryArtifactsImported = true;
    }
    private static string Stamp(string path) => File.Exists(path) ? $"{new FileInfo(path).Length}:{File.GetLastWriteTimeUtc(path).Ticks}" : "-";

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>
/// 备机历史复制落后的可观测计数：超过行数阈值，或连续多轮无法追平，即构成健康告警。
/// </summary>
public sealed class HaHistoryLagMonitor(string subject = "历史复制")
{
    public const long WarnRows = 10_000;
    public const int WarnStreak = 3;
    public long Behind { get; private set; }
    public int Streak { get; private set; }
    public IReadOnlyList<string> Tables { get; private set; } = [];

    public void Observe(long behind, IReadOnlyList<string>? tables = null)
    {
        if (behind <= 0) { Streak = 0; Tables = []; }
        else if (Behind == 0 || behind < Behind) Streak = 1;
        else Streak++;
        Behind = behind;
        if (tables is { Count: > 0 }) Tables = tables.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        else if (behind <= 0) Tables = [];
    }

    public string? Warning()
    {
        if (Behind >= WarnRows) return $"备机{subject}落后 {Behind} 行，超过告警阈值 {WarnRows}";
        if (Streak >= WarnStreak && Behind > 0) return $"备机{subject}落后 {Behind} 行，已连续 {Streak} 轮未能追平";
        return null;
    }

    public static void RecordPromotionGap(HistoryLossLog? log, long behind, long lastSyncTs, long now, string reason)
    {
        if (log is null || behind <= 0) return;
        var from = lastSyncTs > 0 ? lastSyncTs : now;
        log.Record(HistoryGapKind.Rejected, from, now, detail: $"主备接管时历史未同步尾段 {behind} 行（{reason}）");
    }
}
