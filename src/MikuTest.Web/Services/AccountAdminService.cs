using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;

namespace MikuTest.Web.Services;

public sealed record ManagedAccount(
    string Id,
    string UserName,
    string DisplayName,
    string Role,
    AccountStatus Status,
    DateTime CreatedAtUtc,
    string[] Permissions,
    bool CustomPermissions
);

public sealed class AccountAdminService(IServiceScopeFactory scopes, IManagementAccess access)
{
    public async Task<List<ManagedAccount>> ListAsync(bool system = false)
    {
        await access.RequireAsync(system ? Permissions.AdminManage : Permissions.UserView);
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var result = new List<ManagedAccount>();
        foreach (var user in await users.Users.OrderBy(u => u.CreatedAtUtc).ToListAsync())
        {
            var roles = await users.GetRolesAsync(user);
            var role =
                roles.Contains(AppRoles.SuperAdmin) ? AppRoles.SuperAdmin
                : roles.Contains(AppRoles.Administrator) ? AppRoles.Administrator
                : AppRoles.User;
            if (!system && role != AppRoles.User)
                continue;
            var grants =
                role == AppRoles.SuperAdmin ? Permissions.Labels.Keys.ToArray()
                : role == AppRoles.Administrator
                    ? (
                        user.PermissionsConfigured
                            ? (await users.GetClaimsAsync(user))
                                .Where(c => c.Type == Permissions.ClaimType)
                                .Select(c => c.Value)
                                .Intersect(Permissions.Grantable)
                                .ToArray()
                            : Permissions.AdminDefaults
                    )
                : [];
            result.Add(
                new(
                    user.Id,
                    user.UserName ?? user.Id,
                    user.DisplayName,
                    role,
                    user.Status,
                    user.CreatedAtUtc,
                    grants,
                    user.PermissionsConfigured
                )
            );
        }
        return result;
    }

    public async Task SetStatusAsync(string id, AccountStatus status)
    {
        if (!Enum.IsDefined(status))
            throw new InvalidOperationException("账号状态无效。");
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var target = await users.FindByIdAsync(id) ?? throw new InvalidOperationException("账号不存在。");
        var roles = await users.GetRolesAsync(target);
        var permission =
            roles.Contains(AppRoles.Administrator) || roles.Contains(AppRoles.SuperAdmin)
                ? Permissions.AdminManage
            : status == AccountStatus.Deleted || target.Status == AccountStatus.Deleted
                ? Permissions.UserDelete
            : Permissions.UserDisable;
        var actor = await access.RequireAsync(permission);
        Protect(actor, target, roles);
        if (target.Status == status)
            throw new InvalidOperationException("账号已经处于该状态。");
        var before = target.Status;
        target.Status = status;
        IdentitySeed.Ensure(await users.UpdateAsync(target));
        IdentitySeed.Ensure(await users.UpdateSecurityStampAsync(target));
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                status == AccountStatus.Deleted ? "User.SoftDelete"
                    : status == AccountStatus.Suspended ? "User.Suspend"
                    : "User.Restore",
                "User",
                id,
                new { Status = before.ToString() },
                new { Status = status.ToString() }
            )
        );
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task SetAdminAsync(string id, bool makeAdmin)
    {
        var actor = await access.RequireAsync(Permissions.AdminManage);
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var target = await users.FindByIdAsync(id) ?? throw new InvalidOperationException("账号不存在。");
        var roles = await users.GetRolesAsync(target);
        Protect(actor, target, roles);
        if (makeAdmin && target.Status != AccountStatus.Active)
            throw new InvalidOperationException("请先恢复账号为启用状态，再提升管理员。");
        if (makeAdmin == roles.Contains(AppRoles.Administrator))
            throw new InvalidOperationException("管理员身份未发生变化。");
        if (makeAdmin)
            IdentitySeed.Ensure(await users.AddToRoleAsync(target, AppRoles.Administrator));
        else
            IdentitySeed.Ensure(await users.RemoveFromRoleAsync(target, AppRoles.Administrator));
        if (!roles.Contains(AppRoles.User))
            IdentitySeed.Ensure(await users.AddToRoleAsync(target, AppRoles.User));
        var claims = (await users.GetClaimsAsync(target))
            .Where(c => c.Type == Permissions.ClaimType)
            .ToArray();
        if (claims.Length > 0)
            IdentitySeed.Ensure(await users.RemoveClaimsAsync(target, claims));
        target.PermissionsConfigured = false;
        IdentitySeed.Ensure(await users.UpdateAsync(target));
        IdentitySeed.Ensure(await users.UpdateSecurityStampAsync(target));
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                makeAdmin ? "Admin.Promote" : "Admin.Demote",
                "User",
                id,
                new { Roles = roles },
                new { Role = makeAdmin ? AppRoles.Administrator : AppRoles.User }
            )
        );
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task SetPermissionsAsync(string id, IEnumerable<string> values)
    {
        var actor = await access.RequireAsync(Permissions.AdminManage);
        var grants = Permissions.Normalize(values);
        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var target = await users.FindByIdAsync(id) ?? throw new InvalidOperationException("账号不存在。");
        var roles = await users.GetRolesAsync(target);
        Protect(actor, target, roles);
        if (!roles.Contains(AppRoles.Administrator))
            throw new InvalidOperationException("仅可为普通管理员分配权限。");
        var claims = (await users.GetClaimsAsync(target))
            .Where(c => c.Type == Permissions.ClaimType)
            .ToArray();
        var before = target.PermissionsConfigured
            ? claims.Select(c => c.Value).ToArray()
            : Permissions.AdminDefaults;
        if (claims.Length > 0)
            IdentitySeed.Ensure(await users.RemoveClaimsAsync(target, claims));
        IdentitySeed.Ensure(
            await users.AddClaimsAsync(
                target,
                grants.Select(v => new System.Security.Claims.Claim(Permissions.ClaimType, v))
            )
        );
        target.PermissionsConfigured = true;
        IdentitySeed.Ensure(await users.UpdateAsync(target));
        IdentitySeed.Ensure(await users.UpdateSecurityStampAsync(target));
        db.AuditLogs.Add(
            AuditLog.Create(actor, "Admin.Permissions", "User", id, before, grants.Order().ToArray())
        );
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private static void Protect(ManagementActor actor, ApplicationUser target, IList<string> roles)
    {
        if (actor.Id == target.Id)
            throw new InvalidOperationException("不能修改自己的管理身份或账号状态。");
        if (roles.Contains(AppRoles.SuperAdmin))
            throw new InvalidOperationException("网页后台不允许降级、禁用或删除超级管理员。");
    }
}
