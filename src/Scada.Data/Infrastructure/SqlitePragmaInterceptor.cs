using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Scada.Data.Infrastructure;

public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
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

    private static void Apply(DbConnection connection)
    {
        if (IsReadOnly(connection)) return;
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (IsReadOnly(connection)) return;
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // EF probes existing SQLite databases through a read-only connection before migrations.
    // Changing journal mode on that probe prevents restored DELETE-mode databases from opening.
    private static bool IsReadOnly(DbConnection connection) => connection is SqliteConnection sqlite
        && new SqliteConnectionStringBuilder(sqlite.ConnectionString).Mode == SqliteOpenMode.ReadOnly;
}
