using Microsoft.EntityFrameworkCore;
using Scada.Data.RuntimeCleanup;

namespace Scada.Data.Tests;

public sealed class RuntimeCleanupCatalogTests
{
    [Fact]
    public void EveryRuntimeEntityIsCataloguedExactlyOnce()
    {
        var required = RuntimeCleanupCatalog.RequiredRuntimeEntities()
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var catalogued = RuntimeCleanupCatalog.Entries
            .Select(x => x.RuntimeEntity)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(required, catalogued);
        Assert.Equal(catalogued.Length, catalogued.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task MustCleanEntriesHaveHooksAndCanRegisterImplementations()
    {
        var must = RuntimeCleanupCatalog.Entries.Where(x => x.Policy == RuntimeCleanupPolicy.MustClean).ToList();
        Assert.NotEmpty(must);
        Assert.All(must, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Hook));
            Assert.NotEmpty(entry.ConfigEntities);
        });

        var called = 0;
        RuntimeCleanupCatalog.Register("catalog-test-hook", (_, _, _) =>
        {
            called++;
            return Task.CompletedTask;
        });
        Assert.True(RuntimeCleanupCatalog.TryGetHook("catalog-test-hook", out var hook));
        await hook(null!, [], CancellationToken.None);
        Assert.Equal(1, called);
    }

    [Fact]
    public void HistoryTablesAreNotInTheRuntimeCatalog()
    {
        Assert.DoesNotContain(RuntimeCleanupCatalog.Entries, x =>
            x.RuntimeEntity.Contains("raw_", StringComparison.OrdinalIgnoreCase)
            || x.RuntimeEntity.Contains("HistorySample", StringComparison.OrdinalIgnoreCase));
        Assert.Null(typeof(RuntimeDbContext).GetProperty("HistoryValues"));
    }
}
