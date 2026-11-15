using System.Text;

namespace Scada.ScriptWorker;

/// <summary>
/// 请求被宿主拒绝时携带机器可读的错误码，避免把"超限/协议不符"混同为脚本异常。
/// </summary>
internal sealed class WorkerRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Worker 自己的诊断日志。stdout 归行协议、stderr 归宿主，两者都不能当日志用：
/// 进程在初始化阶段就退出时（缺依赖、runtimeconfig 不匹配），这是唯一的排查线索。
/// 目录由宿主通过 SCADA_SCRIPT_LOG_DIR 指定，缺省落到临时目录；单文件上限 1 MiB，写满即停。
/// </summary>
internal static class WorkerLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Sync = new();
    private static StreamWriter? _writer;
    private static long _written;
    private static bool _disabled;

    public static void Open()
    {
        try
        {
            var root = Environment.GetEnvironmentVariable("SCADA_SCRIPT_LOG_DIR");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(Path.GetTempPath(), "scada-script-logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"worker-{Environment.ProcessId}-{DateTime.Now:yyyyMMddHHmmss}.log");
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 日志不可写不能影响脚本执行：退化为静默。
            _disabled = true;
        }
    }

    public static void Write(string level, string message)
    {
        if (_disabled || _writer is null) return;
        lock (Sync)
        {
            if (_disabled || _written > MaxBytes) return;
            try
            {
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
                _writer.WriteLine(line);
                _written += line.Length + Environment.NewLine.Length;
                if (_written > MaxBytes) _writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [warn] log size limit reached, further entries are dropped");
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _disabled = true;
            }
        }
    }

    public static void Write(string level, Exception? exception) =>
        Write(level, exception is null ? "unknown" : $"{exception.GetType().FullName}: {exception.Message}\n{exception.StackTrace}");
}
