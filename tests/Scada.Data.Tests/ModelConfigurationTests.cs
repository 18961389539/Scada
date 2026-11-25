using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Scada.Core.Entities;

namespace Scada.Data.Tests;

/// <summary>
/// OnModelCreating 拆成 88 个 IEntityTypeConfiguration 之后的回归网：
/// 模型必须和拆分前完全一致，且 Config / Runtime 两组配置不能互相串进对方的模型
/// （ApplyConfigurationsFromAssembly 默认会扫整个程序集，漏了命名空间过滤会凭空多出几十张表）。
/// 另有一道 EF 自己的验证：dotnet ef migrations add 生成的探针迁移 Up/Down 为空。
/// </summary>
public sealed class ModelConfigurationTests
{
    [Fact]
    public void ConfigurationsDoNotLeakAcrossContexts()
    {
        using var config = new ConfigDbContext(
            new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite("Data Source=:memory:").Options);
        Assert.NotNull(config.Model.FindEntityType(typeof(Tag)));
        Assert.NotNull(config.Model.FindEntityType(typeof(AuditOutboxEntry)));
        Assert.NotNull(config.Model.FindEntityType(typeof(SiteOperationsSettings)));
        Assert.Null(config.Model.FindEntityType(typeof(AlarmEvent)));

        using var runtime = new RuntimeDbContext(
            new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite("Data Source=:memory:").Options);
        Assert.NotNull(runtime.Model.FindEntityType(typeof(AlarmEvent)));
        Assert.NotNull(runtime.Model.FindEntityType(typeof(OperationLog)));
        Assert.Null(runtime.Model.FindEntityType(typeof(Device)));
        Assert.Null(runtime.Model.FindEntityType(typeof(SiteOperationsSettings)));
    }

    [Fact]
    public void CriticalMappingsSurviveTheSplit()
    {
        using var config = new ConfigDbContext(
            new DbContextOptionsBuilder<ConfigDbContext>().UseSqlite("Data Source=:memory:").Options);
        // EF 10 的运行时模型是 read-optimized 的，CheckConstraint 只存在于设计期模型里。
        var designModel = config.GetService<IDesignTimeModel>().Model;
        var tag = designModel.FindEntityType(typeof(Tag))!;
        Assert.Equal("Tag", tag.GetTableName());
        Assert.Contains(tag.GetCheckConstraints(), c => c.Name == "CK_Tag_Source");
        Assert.Equal("AuditOutbox", config.Model.FindEntityType(typeof(AuditOutboxEntry))!.GetTableName());

        using var runtime = new RuntimeDbContext(
            new DbContextOptionsBuilder<RuntimeDbContext>().UseSqlite("Data Source=:memory:").Options);
        var log = runtime.Model.FindEntityType(typeof(OperationLog))!;
        Assert.Contains(log.GetIndexes(),
            index => index.IsUnique && index.Properties.Any(p => p.Name == nameof(OperationLog.SourceId)));
    }

    [Fact]
    public void EveryConfigurationIsPublicAndDiscoverable()
    {
        var types = typeof(ConfigDbContext).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetInterfaces().Any(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEntityTypeConfiguration<>)))
            .ToArray();
        Assert.Equal(89, types.Length);
        Assert.All(types, t => Assert.True(t.IsPublic));
        Assert.Equal(54, types.Count(t => t.Namespace == "Scada.Data.Configurations.Config"));
        Assert.Equal(35, types.Count(t => t.Namespace == "Scada.Data.Configurations.Runtime"));
    }
}
