namespace Scada.Engine.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir(string prefix = "scada-test-")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* 句柄释放失败不影响断言 */ }
    }
}