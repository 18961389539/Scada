using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Scada.Core.Interfaces;
using Scada.Core.Options;

namespace Scada.Data;

/// <summary>
/// 跨进程配置刷新。配置库被多个宿主同时打开（Server 写、Collector/看板读），
/// 只有执行写入的那个进程会主动 ReloadAsync，其它进程此前要重启才看得到改动。
/// 这里用一条长期持有的连接读 <c>PRAGMA data_version</c>：它只在"别的连接提交了改动"时变化，
/// 不产生写入、不占写锁，比轮询时间戳或版本表都便宜。
/// 间隔取 <see cref="ScadaSettings.ConfigRefreshSeconds"/>，小于等于 0 表示关闭。
/// </summary>
public sealed class ConfigRefreshService(
    IConfigSnapshot snapshot,
    AccessModelProvider access,
    IOptions<ScadaSettings> settings,
    ILogger<ConfigRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(settings.Value.ConfigRefreshSeconds);
        if (interval <= TimeSpan.Zero) return;

        SqliteConnection connection;
        try
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(settings.Value.ConfigDbPath),
                // 不用 ReadOnly：WAL 库只读打开需要目录可写（-shm），现场常见只给文件读权限。
                // 这条连接本身只读 pragma、不写事务，不会与写入方抢锁。
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ConnectionString);
            await connection.OpenAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            // 只读打开失败（权限、文件未就绪）不该影响宿主启动：配置本来就有启动时的那一次加载。
            logger.LogWarning(ex, "配置刷新轮询未启动，跨进程配置变更需要重启后才生效");
            return;
        }

        await using (connection)
        {
            var last = DataVersion(connection);
            try
            {
                using var timer = new PeriodicTimer(interval);
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    var current = DataVersion(connection);
                    if (current == last) continue;
                    last = current;
                    await snapshot.ReloadAsync(stoppingToken);
                    // 授权模型跟着配置走：用户、角色、区域分配变了就一起失效。
                    access.Invalidate();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常停机。
            }
        }
    }

    /// <summary>读取 SQLite 的数据版本号；变化即表示其它连接提交过写入。</summary>
    public static long DataVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }
}
