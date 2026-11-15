using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Scada.Data.Infrastructure;

/// <summary>
/// SQLite 事务的唯一入口。存在的理由是：<c>deferred</c> 这个参数只在
/// <see cref="SqliteConnection.BeginTransaction(bool)"/> 上有，EF Core 的
/// <c>Database.BeginTransactionAsync()</c> 没有暴露它，所以必须强转成 SQLite 连接。
///
/// 把强转收在这里有两个好处：
/// <list type="bullet">
///   <item>「为什么这里要指定 deferred」的知识只写一遍。此前散落在 AssetEndpoints 与 GatewayCenterService，
///   两处注释各说各的，改一处漏一处。</item>
///   <item>将来换库（例如从 SQLite 迁到 PostgreSQL）时，只有这一个文件编译失败，而不是全仓搜索强转。</item>
/// </list>
///
/// 语义差异（SQLite 文档）：
/// <list type="bullet">
///   <item><c>deferred: true</c> → <c>BEGIN</c>：事务开始不取写锁，第一次写入才升级。
///   适合"先读一批再写"且冲突可重试的场景（如网关中心按需读取后落库）。</item>
///   <item><c>deferred: false</c> → <c>BEGIN IMMEDIATE</c>：立刻取保留锁。
///   适合确定要写的场景：SQLite 不支持锁升级重试，读事务转写时会直接拿到 SQLITE_BUSY，
///   而 IMMEDIATE 让冲突发生在事务开头，调用方可以干净地重试整段。</item>
/// </list>
/// </summary>
public static class SqliteTx
{
    /// <summary>开始一个延迟取锁的事务（<c>BEGIN</c>）。</summary>
    public static SqliteTransaction BeginDeferred(DbContext db) => Begin(db, deferred: true);

    /// <summary>开始一个立即取写锁的事务（<c>BEGIN IMMEDIATE</c>）。</summary>
    public static SqliteTransaction BeginImmediate(DbContext db) => Begin(db, deferred: false);

    private static SqliteTransaction Begin(DbContext db, bool deferred)
    {
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new InvalidOperationException("SqliteTx 只能用于 SQLite 提供程序；当前连接的提供程序不是 Microsoft.Data.Sqlite。");

        // BeginTransaction 要求连接已打开。EF 在首次查询后才打开连接，直接调用会得到
        // "Execute requires the command to have a transaction"，所以这里显式通过 EF 打开，
        // 让 EF 自己记录引用计数，避免与它的连接生命周期管理打架。
        if (connection.State != ConnectionState.Open)
            db.Database.OpenConnection();

        return connection.BeginTransaction(deferred);
    }
}
