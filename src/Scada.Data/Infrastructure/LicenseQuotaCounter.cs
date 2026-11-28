using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Scada.Core.Options;

namespace Scada.Data.Infrastructure;

/// <summary>
/// 授权配额的设备/测点计数缓存。
///
/// 为什么必须是单例：<see cref="LicenseQuotaInterceptor"/> 注册在 DbContextOptions 的构建回调里，
/// 每个 DbContext 都会新建一个拦截器实例，实例字段无法跨请求复用。以前的做法是每次
/// <c>SaveChanges</c> 都新开一个上下文做两次全表 COUNT，批量导入测点时会被放大成上千次 COUNT。
///
/// 关键约束：保存路径**绝不**在这里新建连接。配置库保存可能发生在调用方打开的显式事务里，
/// 此时新连接初始化会执行 <c>PRAGMA journal_mode=WAL</c>，需要独占锁，会一直等到命令超时
/// （集成测试表现为一个保存卡 30 秒）。因此保存路径只读缓存，不做任何 I/O。
///
/// 预热由 <see cref="DatabaseInitializer.InitializeAsync"/> 在迁移完成后调用一次：
/// 那时没有业务事务，服务也还没开始接收请求，不会出现"首次写入放过超限"的窗口。
///
/// 缓存 60 秒后标记过期并在后台重算，以收敛多实例/HA 计数漂移。过期后校验仍使用内存里的旧计数，
/// 不能把过期当成未命中而放行。写入成功后由拦截器按增量修正，因此单实例内的连续写入不会触发重算。
/// </summary>
    // SemaphoreSlim 只有用到 AvailableWaitHandle 才分配内核句柄；本类只用 WaitAsync/Release，
    // 没有需要释放的非托管资源，CA1001 在这里是误报。
    [SuppressMessage("Microsoft.Usage", "CA1001:Types that own disposable fields should be disposable", Justification = "SemaphoreSlim 仅使用 WaitAsync/Release，不分配等待句柄")]
public sealed class LicenseQuotaCounter(IServiceProvider services)
{
    private static readonly long TtlMs = TtlMilliseconds;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private int _devices = -1, _tags = -1;
    private long _loadedAt;

    // 缓存是否在 TTL 内。后台重算用这个判断要不要再查库。
    public bool TryGet(out int devices, out int tags)
    {
        lock (_sync)
        {
            if (HasValue && !IsExpired)
            {
                devices = _devices;
                tags = _tags;
                return true;
            }
            devices = 0;
            tags = 0;
            return false;
        }
    }

    /// <summary>
    /// 校验用：TTL 过期仍返回上次计数，避免把过期当成「未命中而放行」。
    /// 从未预热时返回 false。
    /// </summary>
    public bool TryGetCached(out int devices, out int tags)
    {
        lock (_sync)
        {
            if (!HasValue)
            {
                devices = 0;
                tags = 0;
                return false;
            }
            devices = _devices;
            tags = _tags;
            return true;
        }
    }

    public bool IsFresh
    {
        get { lock (_sync) return HasValue && !IsExpired; }
    }

    /// <summary>TTL 过期后在后台重算；保存路径只调用它，自身不做 I/O。</summary>
    public void RequestRefresh(CancellationToken ct = default)
    {
        if (IsFresh) return;
        _ = RefreshInBackgroundAsync(ct);
    }

    private async Task RefreshInBackgroundAsync(CancellationToken ct)
    {
        try { await GetAsync(ct).ConfigureAwait(false); }
        catch (Exception) { /* 下次保存或下次请求再试 */ }
    }

    private bool HasValue => _devices >= 0 && _tags >= 0;
    private bool IsExpired => Environment.TickCount64 - _loadedAt >= TtlMs;

    internal const long TtlMilliseconds = 60_000;

    internal void SeedForTests(int devices, int tags)
    {
        lock (_sync)
        {
            _devices = devices;
            _tags = tags;
            _loadedAt = Environment.TickCount64;
        }
    }

    internal void ExpireForTests()
    {
        lock (_sync)
        {
            if (HasValue) _loadedAt = Environment.TickCount64 - TtlMs - 1;
        }
    }

    /// <summary>
    /// 从配置库重算计数。只允许在启动预热或后台线程调用，不能在保存路径上调用。
    ///
    /// 不用 EF 上下文：新建 EF 连接会走 <c>SqlitePragmaInterceptor</c> 执行
    /// <c>PRAGMA journal_mode=WAL</c>，那需要独占锁，遇到调用方的事务会一直等到命令超时。
    /// 这里用一条只读 SELECT 直连：WAL 下读不阻塞写，真遇到锁也最多等 1 秒就放弃。
    /// </summary>
    public async ValueTask<(int Devices, int Tags)> GetAsync(CancellationToken ct)
    {
        if (TryGet(out var devices, out var tags)) return (devices, tags);
        await _gate.WaitAsync(ct);
        try
        {
            if (TryGet(out devices, out tags)) return (devices, tags);
            (devices, tags) = await CountAsync(ct);
            lock (_sync)
            {
                _devices = devices;
                _tags = tags;
                _loadedAt = Environment.TickCount64;
            }
            return (devices, tags);
        }
        finally { _gate.Release(); }
    }

    private async Task<(int Devices, int Tags)> CountAsync(CancellationToken ct)
    {
        var settings = services.GetRequiredService<IOptions<ScadaSettings>>().Value;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(settings.ConfigDbPath),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT COUNT(*) FROM Device), (SELECT COUNT(*) FROM Tag)";
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    /// <summary>保存成功后按增量修正缓存；删除设备/测点会释放额度，因此增量可以是负数。</summary>
    public void Adjust(int addDevices, int addTags)
    {
        lock (_sync)
        {
            // 缓存本来就无效时不填数：下次校验仍然放行，等 TTL 后的预热或重启再收敛。
            if (_devices >= 0) _devices = Math.Max(0, _devices + addDevices);
            if (_tags >= 0) _tags = Math.Max(0, _tags + addTags);
            _loadedAt = Environment.TickCount64;
        }
    }
}
