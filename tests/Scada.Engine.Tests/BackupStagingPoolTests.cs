using Microsoft.Data.Sqlite;
using Scada.Engine.Maintenance;

namespace Scada.Engine.Tests;

public class BackupStagingPoolTests
{
    [Fact]
    public void BackupServiceSourceMustNotClearAllPools()
    {
        var path = FindSource("src/Scada.Engine/Maintenance/BackupService.cs");
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("SqliteConnection.ClearAllPools", text);
        Assert.Contains("ClearStagingPools", text);
    }

    [Fact]
    public void ClearStagingPoolsOnlyTouchesFilesUnderTheDirectory()
    {
        var staging = Path.Combine(Path.GetTempPath(), "scada-drill-pool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var db = Path.Combine(staging, "history", "agg_2000.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        using (var connection = new SqliteConnection($"Data Source={db}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "create table t(id int);";
            cmd.ExecuteNonQuery();
        }
        BackupService.ClearStagingPools(staging);
        BackupService.ClearStagingPools(staging + "-missing");
        Directory.Delete(staging, true);
    }

    private static string FindSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
