namespace Scada.Server.Services;

// 同一数据目录只允许一个服务进程：独占打开 data/.instance.lock，持有到进程退出。
// 便携部署双击启动（WinExe）时，第二次启动靠它立刻给出明确提示，而不是走到端口绑定才失败。
// 备机/主机必须是不同机器上的不同数据目录，互不影响。
internal static class SingleInstanceGuard
{
    public const string LockFileName = ".instance.lock";

    public static FileStream? TryAcquire(string dataRoot)
    {
        try
        {
            return new FileStream(Path.Combine(dataRoot, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}