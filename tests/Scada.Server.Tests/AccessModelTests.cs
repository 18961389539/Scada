using System.Text.Json;
using Scada.Core.Enums;
using Scada.Core.Security;

namespace Scada.Server.Tests;

public sealed class AccessModelTests
{
    private static AccessModel Model(params (int UserId, Permission P, int? Area)[] grants) => AccessModel.Build(
        devices: [(10, 2), (20, 3)],
        tags: [(100, TagKind.Physical, 10, null), (200, TagKind.Physical, 20, null), (300, TagKind.Calculated, null, null), (400, TagKind.Calculated, null, 3), (500, TagKind.Calculated, null, null)],
        dependencies: [(300, 100), (300, 200), (500, 100)],
        users: [(1, UserRole.None, true), (2, UserRole.Viewer, true), (3, UserRole.Admin, false), (4, UserRole.None, true)],
        assignments: grants.Select(g => (g.UserId, g.P, g.Area)),
        builtAt: 0);

    [Fact]
    public void BuiltInRolesKeepTheirMeaningAcrossAllAreas()
    {
        var viewer = Model().For(2);
        Assert.True(viewer.CanTag(Permission.View, 100));
        Assert.True(viewer.CanTag(Permission.View, 200));
        Assert.False(viewer.CanTag(Permission.Control, 100));
        Assert.False(Model().For(3).HasAny(Permission.View)); // 停用的管理员没有任何权限
        Assert.Equal(Permission.View | Permission.Control | Permission.AckAlarm, Permissions.ForBuiltIn(UserRole.Operator));
    }

    [Fact]
    public void CustomRoleCombinationsAreScopedPerArea()
    {
        var access = Model((1, Permission.View | Permission.Control, 2), (1, Permission.View | Permission.AckAlarm, 3)).For(1);
        Assert.True(access.CanTag(Permission.Control, 100));
        Assert.False(access.CanTag(Permission.Control, 200));
        Assert.True(access.CanTag(Permission.AckAlarm, 200));
        Assert.False(access.CanTag(Permission.AckAlarm, 100));
        Assert.True(access.HasAny(Permission.Control));
        Assert.False(access.HasGlobal(Permission.View));
        Assert.Equal(new[] { 2, 3 }, access.AreasWith(Permission.View)!.OrderBy(x => x));
        Assert.False(access.CanDevice(Permission.View, 99)); // 未知设备只对全局授权开放
    }

    [Fact]
    public void CalculatedTagNeedsAllInputAreasUnlessExplicit()
    {
        var onlyA = Model((1, Permission.View, 2)).For(1);
        Assert.False(onlyA.CanTag(Permission.View, 300)); // 输入跨 A/B
        Assert.True(onlyA.CanTag(Permission.View, 500));  // 输入只在 A
        Assert.False(onlyA.CanTag(Permission.View, 400)); // 显式指定 B
        var both = Model((1, Permission.View, 2), (1, Permission.View, 3)).For(1);
        Assert.True(both.CanTag(Permission.View, 300));
        Assert.True(both.CanTag(Permission.View, 400));
    }

    [Fact]
    public void UserWithoutGrantsCannotDoAnything()
    {
        var none = Model().For(4);
        Assert.Empty(none.Grants);
        Assert.False(none.CanTag(Permission.View, 100));
        Assert.False(Model().For(null).HasAny(Permission.View));
    }

    [Fact]
    public void AuditDiffListsOnlyChangedFieldsAndMasksSecrets()
    {
        var before = new { Name = "泵1", Port = 502, Host = "a", PasswordHash = "h1" };
        var after = new { Name = "泵1", Port = 503, Host = "a", PasswordHash = "h2" };
        using var doc = JsonDocument.Parse(AuditDiff.Compute(before, after));
        var changes = doc.RootElement.GetProperty("changes");
        Assert.Equal("update", doc.RootElement.GetProperty("op").GetString());
        Assert.Equal(502, changes.GetProperty("port").GetProperty("old").GetInt32());
        Assert.Equal(503, changes.GetProperty("port").GetProperty("new").GetInt32());
        Assert.False(changes.TryGetProperty("name", out _));
        Assert.Equal("***", changes.GetProperty("passwordHash").GetProperty("new").GetString());
        Assert.DoesNotContain("h2", doc.RootElement.GetRawText());
        using var created = JsonDocument.Parse(AuditDiff.Compute(null, new { Name = "x" }));
        Assert.Equal("create", created.RootElement.GetProperty("op").GetString());
        using var deleted = JsonDocument.Parse(AuditDiff.Compute(new { Name = "x" }, null));
        Assert.Equal("delete", deleted.RootElement.GetProperty("op").GetString());
    }
}
