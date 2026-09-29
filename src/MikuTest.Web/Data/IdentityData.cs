using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Services;

namespace MikuTest.Web.Data;

public class ApplicationUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public AccountStatus Status { get; set; } = AccountStatus.Active;
    public bool PermissionsConfigured { get; set; }
}

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SiteConfiguration> SiteConfigurations => Set<SiteConfiguration>();
}

public static class AppRoles
{
    public const string User = "User",
        Administrator = "Admin",
        SuperAdmin = "SuperAdmin";
}

public static class IdentitySeed
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration)
    {
        var db = services.GetRequiredService<IdentityDbContext>();
        await ManagementSchema.UpgradeIdentityAsync(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var roles = services.GetRequiredService<RoleManager<IdentityRole>>();
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var legacy = await roles.FindByNameAsync("Administrator");
        if (legacy is not null)
        {
            var current = await roles.FindByNameAsync(AppRoles.Administrator);
            if (current is null)
            {
                legacy.Name = AppRoles.Administrator;
                Ensure(await roles.UpdateAsync(legacy));
            }
            else
            {
                foreach (var member in await users.GetUsersInRoleAsync("Administrator"))
                {
                    if (!await users.IsInRoleAsync(member, AppRoles.Administrator))
                        Ensure(await users.AddToRoleAsync(member, AppRoles.Administrator));
                    Ensure(await users.RemoveFromRoleAsync(member, "Administrator"));
                }
            }
        }
        foreach (var name in new[] { AppRoles.User, AppRoles.Administrator, AppRoles.SuperAdmin })
            if (!await roles.RoleExistsAsync(name))
                Ensure(await roles.CreateAsync(new(name)));
        var admin = (await roles.FindByNameAsync(AppRoles.Administrator))!;
        var claims = await roles.GetClaimsAsync(admin);
        if (!claims.Any(c => c.Type == "PermissionSchema"))
        {
            foreach (var value in Permissions.AdminDefaults)
                if (!claims.Any(c => c.Type == Permissions.ClaimType && c.Value == value))
                    Ensure(await roles.AddClaimAsync(admin, new(Permissions.ClaimType, value)));
            Ensure(await roles.AddClaimAsync(admin, new("PermissionSchema", "1")));
        }
        foreach (var user in await users.Users.ToListAsync())
            if ((await users.GetRolesAsync(user)).Count == 0)
                Ensure(await users.AddToRoleAsync(user, AppRoles.User));
        // Bootstrap once: never reset credentials or re-promote a demoted account on restart.
        if ((await users.GetUsersInRoleAsync(AppRoles.SuperAdmin)).Count == 0)
        {
            var existingName = configuration["SuperAdminAccount:ExistingUserName"]?.Trim();
            if (string.IsNullOrEmpty(existingName))
                existingName = (
                    configuration["AdminAccount:UserName"] ?? configuration["AdminAccount:Email"]
                )?.Trim();
            var newName = configuration["SuperAdminAccount:UserName"]?.Trim();
            var password = configuration["SuperAdminAccount:Password"];
            ApplicationUser? owner = null;
            if (!string.IsNullOrEmpty(existingName))
            {
                owner = await users.FindByNameAsync(existingName);
                if (
                    owner is not null
                    && (
                        !await users.IsInRoleAsync(owner, AppRoles.Administrator)
                        || owner.Status != AccountStatus.Active
                    )
                )
                    throw new InvalidOperationException(
                        "超级管理员初始化仅允许指定现有的启用中管理员，不能自动提升普通账号。"
                    );
            }
            else if (!string.IsNullOrEmpty(newName) && !string.IsNullOrEmpty(password))
            {
                if (await users.FindByNameAsync(newName) is not null)
                    throw new InvalidOperationException(
                        "初始化用户名已存在，请使用全新用户名，或通过 ExistingUserName 指定现有管理员。"
                    );
                owner = new()
                {
                    UserName = newName,
                    DisplayName = configuration["SuperAdminAccount:DisplayName"] ?? "MikuTest 超级管理员",
                };
                Ensure(await users.CreateAsync(owner, password));
            }
            if (owner is not null)
            {
                Ensure(await users.AddToRoleAsync(owner, AppRoles.SuperAdmin));
                Ensure(await users.UpdateSecurityStampAsync(owner));
                db.AuditLogs.Add(
                    AuditLog.Create(
                        new("system", "初始化"),
                        "SuperAdmin.Bootstrap",
                        "User",
                        owner.Id,
                        after: new { owner.UserName, Role = AppRoles.SuperAdmin }
                    )
                );
                await db.SaveChangesAsync();
            }
        }
        await transaction.CommitAsync();
    }

    internal static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("；", result.Errors.Select(e => e.Description)));
    }
}
