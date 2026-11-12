using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Scada.Data.Infrastructure;

/// <summary>
/// 连接打开时统一设置 WAL。持久化级别按库分级：
/// 配置库与运行库用 FULL（配置改动、报警/命令回执与审计要扛掉电）；
/// 历史库保持 NORMAL（万点级写入的 fsync 是瓶颈，掉电最多丢最后一个未提交批次）。
/// </summary>
public sealed class SqlitePragmaInterceptor(bool fullDurability = false) : DbConnectionInterceptor
{
    private readonly string _pragmas = fullDurability
        ? "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;"
        : "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";

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
