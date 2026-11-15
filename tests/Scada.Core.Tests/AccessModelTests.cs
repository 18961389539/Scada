using Scada.Core.Entities;
using Scada.Core.Enums;
using Scada.Core.Security;

namespace Scada.Core.Tests;

public class AccessModelTests
{
    [Fact]
    public void CalculatedTagInheritsAreasOfItsInputs()
    {
        var tags = new (int Id, TagKind Kind, int? DeviceId, int? AreaId)[]
        {
            (1, TagKind.Physical, 5, null),
            (2, TagKind.Calculated, null, null),
            (3, TagKind.Calculated, null, null)
        };
        var dependencies = new (int TagId, int InputTagId)[] { (2, 1), (3, 2) };

        var model = AccessModel.Build([(5, 2)], tags, dependencies, [], [], 0);

        Assert.Equal(new[] { 2 }, model.AreasOfTag(3));
    }

    [Fact]
    public void DeepCalculatedChainIsTruncatedInsteadOfOverflowingTheStack()
    {
        // 工程包可以导入任意深度：权限模型在启动/刷新路径上构建，没有上限时一条构造过的
        // 万级依赖链会直接打栈溢出（进程崩溃，不是一次可捕获的异常）。
        const int count = 5000;
        var tags = Enumerable.Range(1, count)
            .Select(id => (Id: id, Kind: TagKind.Calculated, DeviceId: (int?)null, AreaId: (int?)null))
            .ToArray();
        var dependencies = Enumerable.Range(2, count - 1)
            .Select(id => (TagId: id, InputTagId: id - 1))
            .ToArray();

        var model = AccessModel.Build([], tags, dependencies, [], [], 0);

        Assert.Equal(AccessModel.MaxDependencyDepth, 64);
        Assert.Equal(new[] { Area.DefaultId }, model.AreasOfTag(count));
    }

    [Fact]
    public void CycleFallsBackToDefaultArea()
    {
        var tags = new (int Id, TagKind Kind, int? DeviceId, int? AreaId)[]
        {
            (1, TagKind.Calculated, null, null),
            (2, TagKind.Calculated, null, null)
        };
        var dependencies = new (int TagId, int InputTagId)[] { (1, 2), (2, 1) };

        var model = AccessModel.Build([], tags, dependencies, [], [], 0);

        Assert.Equal(new[] { Area.DefaultId }, model.AreasOfTag(1));
    }

    [Fact]
    public void AreaGrantOnlyAppliesToThatArea()
    {
        var tags = new (int Id, TagKind Kind, int? DeviceId, int? AreaId)[] { (1, TagKind.Physical, 5, null), (2, TagKind.Physical, 6, null) };
        var model = AccessModel.Build([(5, 2), (6, 3)], tags, [], [(1, UserRole.None, true)], [(1, Permission.View, 2)], 0);
        var context = model.For(1);

        Assert.True(context.CanTag(Permission.View, 1));
        Assert.False(context.CanTag(Permission.Control, 1));
        Assert.False(context.CanTag(Permission.View, 2));
        Assert.Equal(new[] { 2 }, context.AreasWith(Permission.View)!);
    }
}
