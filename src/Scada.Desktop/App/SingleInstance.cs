namespace Scada.Desktop.App;

/// <summary>按 Windows 会话隔离的单实例互斥体；允许多个 RDP 用户各自运行一份。</summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex? _mutex;

    public SingleInstance(string name)
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            IsOwner = createdNew;
        }
        catch
        {
            // 无法创建互斥体时不阻塞启动，宁可多开也不能让现场完全打不开。
            _mutex = null;
            IsOwner = true;
        }
    }

    public bool IsOwner { get; }

    public void Dispose()
    {
        if (_mutex is null) return;
        try
        {
            if (IsOwner) _mutex.ReleaseMutex();
        }
        catch
        {
            // 进程退出时释放失败无需处理。
        }
        _mutex.Dispose();
    }
}
