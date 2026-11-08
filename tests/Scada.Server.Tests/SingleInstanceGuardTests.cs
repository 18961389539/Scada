using Scada.Server.Services;

namespace Scada.Server.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void OnlyOneHolderPerDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "scada-single-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var first = SingleInstanceGuard.TryAcquire(root);
            Assert.NotNull(first);
            try
            {
                // 第二个持有者（第二次双击启动）必须拿不到锁。
                Assert.Null(SingleInstanceGuard.TryAcquire(root));
            }
            finally
            {
                first.Dispose();
            }

            // 进程退出/释放后可以再次启动。
            using var again = SingleInstanceGuard.TryAcquire(root);
            Assert.NotNull(again);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DifferentDataRootsDoNotConflict()
    {
        var rootA = Path.Combine(Path.GetTempPath(), "scada-single-a-" + Guid.NewGuid().ToString("N"));
        var rootB = Path.Combine(Path.GetTempPath(), "scada-single-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        try
        {
            using var a = SingleInstanceGuard.TryAcquire(rootA);
            using var b = SingleInstanceGuard.TryAcquire(rootB);
            Assert.NotNull(a);
            Assert.NotNull(b);
        }
        finally
        {
            Directory.Delete(rootA, recursive: true);
            Directory.Delete(rootB, recursive: true);
        }
    }
}