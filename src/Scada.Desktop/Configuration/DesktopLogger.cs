using System.Text;

namespace Scada.Desktop.Configuration;

/// <summary>
/// 极简滚动日志：现场排障只需要时间、级别和异常，不引入额外日志框架。
/// 日志内容禁止包含密码、令牌和加密后的密码文本。
/// </summary>
internal static class DesktopLogger
{
    private static readonly object Gate = new();
    private static string? _directory;

    public static void Initialize(string directory)
    {
        lock (Gate)
        {
            _directory = directory;
            TryDeleteOldFiles(directory);
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var directory = _directory;
            if (string.IsNullOrWhiteSpace(directory)) return;
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"desktop-{DateTime.Now:yyyyMMdd}.log");
            var builder = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);
            if (exception is not null)
            {
                builder.AppendLine().Append(exception);
            }
            lock (Gate)
            {
                File.AppendAllText(file, builder.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能反过来影响客户端运行。
        }
    }

    private static void TryDeleteOldFiles(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return;
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.EnumerateFiles(directory, "desktop-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // 清理失败不影响启动。
        }
    }
}
