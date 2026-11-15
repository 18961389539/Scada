using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Scada.Data.Infrastructure;

/// <summary>
/// 连接打开时统一设置连接级 pragma。持久化级别按库分级：
/// 配置库与运行库用 FULL（配置改动、报警/命令回执与审计要扛掉电）；
/// 历史库保持 NORMAL（万点级写入的 fsync 是瓶颈，掉电最多丢最后一个未提交批次）。
///
/// 外键此前完全依赖驱动默认值，但 schema 重建与运行表搬迁都建立在"外键是否生效"这个前提上，
/// 这里显式声明，避免换驱动版本后语义漂移。busy_timeout 也显式设置：
/// 四个宿主进程同时写同一个库时，锁等待必须由 SQLite 层兜住，而不是靠命令超时反复重试。
/// </summary>
public sealed class SqlitePragmaInterceptor(bool fullDurability = false, int cacheKb = 8_000, long mmapBytes = 0) : DbConnectionInterceptor
{
    private readonly string _pragmas =
        "PRAGMA journal_mode=WAL;"
        + (fullDurability ? " PRAGMA synchronous=FULL;" : " PRAGMA synchronous=NORMAL;")
        + " PRAGMA foreign_keys=ON;"
        + " PRAGMA busy_timeout=5000;"
        + $" PRAGMA cache_size=-{cacheKb};"
        + " PRAGMA temp_store=MEMORY;"
        + (mmapBytes > 0 ? $" PRAGMA mmap_size={mmapBytes};" : "");

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void Apply(DbConnection connection)
    {
        if (IsReadOnly(connection)) return;
        using var command = connection.CreateCommand();
        command.CommandText = _pragmas;
        command.ExecuteNonQuery();
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (IsReadOnly(connection)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = _pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // EF probes existing SQLite databases through a read-only connection before migrations.
    // Changing journal mode on that probe prevents restored DELETE-mode databases from opening.
    private static bool IsReadOnly(DbConnection connection) => connection is SqliteConnection sqlite
        && new SqliteConnectionStringBuilder(sqlite.ConnectionString).Mode == SqliteOpenMode.ReadOnly;
}
