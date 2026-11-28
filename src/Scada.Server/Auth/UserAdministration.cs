using System.Security.Claims;
using Scada.Core.Enums;
using Scada.Core.Security;

namespace Scada.Server.Auth;

/// <summary>
/// 内置管理员与自定义「用户管理」角色的边界：
/// 只有内置 Admin 才能创建/提升管理员，或授予用户管理、系统权限。
/// </summary>
public static class UserAdministration
{
    public const Permission PrivilegedPermissions = Permission.ManageUsers | Permission.System;

    public const string DenyPromoteAdmin = "只有内置管理员才能创建或提升管理员";
    public const string DenyGrantPrivileged = "只有内置管理员才能授予用户管理或系统权限";
    public const string DenyManageAdmin = "只有内置管理员才能管理管理员账号";
    public const string DenyDeleteLastEnabledAdmin = "不能删除最后一个启用中的管理员";

    public static bool IsBuiltInAdmin(ClaimsPrincipal user) =>
        user.IsInRole(nameof(UserRole.Admin));

    public static bool IsPrivileged(Permission permissions) =>
        permissions.HasFlag(Permission.ManageUsers) || permissions.HasFlag(Permission.System);
}
