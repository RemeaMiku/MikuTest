using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;

namespace MikuTest.Web.Services;

public static class Permissions
{
    public const string ClaimType = "Permission",
        AdminAccess = "Admin.Access",
        SuperAccess = "SuperAdmin.Access";
    public const string QuestionView = "Question.View",
        QuestionCreate = "Question.Create",
        QuestionEdit = "Question.Edit",
        QuestionDelete = "Question.Delete",
        QuizManage = "Quiz.Manage",
        UserView = "User.View",
        UserDisable = "User.Disable",
        UserDelete = "User.Delete",
        AdminManage = "Admin.Manage",
        SystemSettings = "System.Settings",
        AuditView = "Audit.View";
    public static readonly Dictionary<string, string> Labels = new()
    {
        [QuestionView] = "浏览题库与材料",
        [QuestionCreate] = "新增题目与材料",
        [QuestionEdit] = "编辑题目与材料",
        [QuestionDelete] = "删除题目与材料",
        [QuizManage] = "管理与发布测试",
        [UserView] = "查看普通账号",
        [UserDisable] = "禁用与启用普通账号",
        [UserDelete] = "软删除与恢复普通账号",
        [AdminManage] = "管理管理员与权限",
        [SystemSettings] = "修改系统设置",
        [AuditView] = "查看审计日志",
    };
    public static readonly string[] Grantable =
    [
        QuestionView,
        QuestionCreate,
        QuestionEdit,
        QuestionDelete,
        QuizManage,
        UserView,
        UserDisable,
        UserDelete,
    ];
    public static readonly string[] AdminDefaults =
    [
        QuestionView,
        QuestionCreate,
        QuestionEdit,
        QuestionDelete,
        QuizManage,
        UserView,
        UserDisable,
    ];

    public static HashSet<string> Normalize(IEnumerable<string> values)
    {
        var result = values.ToHashSet(StringComparer.Ordinal);
        if (result.Except(Grantable).Any())
            throw new InvalidOperationException("包含不可授予普通管理员的权限。");
        if (result.Overlaps([QuestionCreate, QuestionEdit, QuestionDelete, QuizManage]))
            result.Add(QuestionView);
        if (result.Overlaps([UserDisable, UserDelete]))
            result.Add(UserView);
        return result;
    }

    public static void Configure(AuthorizationOptions options)
    {
        foreach (var name in Labels.Keys.Concat([AdminAccess, SuperAccess]))
            options.AddPolicy(
                name,
                p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(name))
            );
    }
}

public sealed record ManagementActor(string Id, string Name, string? IpAddress = null);

public sealed record AccessSnapshot(ManagementActor Actor, string Role, HashSet<string> Permissions)
{
    public bool Allows(string permission) =>
        Role == AppRoles.SuperAdmin
            ? PermissionsClass(permission)
            : Role == AppRoles.Administrator
                && (
                    permission == global::MikuTest.Web.Services.Permissions.AdminAccess
                    || Permissions.Contains(permission)
                );

    private static bool PermissionsClass(string permission) =>
        global::MikuTest.Web.Services.Permissions.Labels.ContainsKey(permission)
        || permission
            is global::MikuTest.Web.Services.Permissions.AdminAccess
                or global::MikuTest.Web.Services.Permissions.SuperAccess;
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionResolver(IServiceScopeFactory scopes)
{
    public async Task<AccessSnapshot?> ResolveAsync(ClaimsPrincipal principal)
    {
        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal.Identity?.IsAuthenticated != true || id is null)
            return null;
        await using var scope = scopes.CreateAsyncScope();
        // 每次管理操作重读账号与安全戳，让撤权或禁用立即生效。
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(id);
        if (user is null || user.Status != AccountStatus.Active)
            return null;
        var options = scope
            .ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<IdentityOptions>>()
            .Value;
        if (principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType) != user.SecurityStamp)
            return null;
        var roles = await users.GetRolesAsync(user);
        var role =
            roles.Contains(AppRoles.SuperAdmin) ? AppRoles.SuperAdmin
            : roles.Contains(AppRoles.Administrator) ? AppRoles.Administrator
            : AppRoles.User;
        var permissions = new HashSet<string>();
        if (role == AppRoles.SuperAdmin)
            permissions.UnionWith(global::MikuTest.Web.Services.Permissions.Labels.Keys);
        else if (role == AppRoles.Administrator)
        {
            if (user.PermissionsConfigured)
                permissions.UnionWith(
                    (await users.GetClaimsAsync(user))
                        .Where(c => c.Type == global::MikuTest.Web.Services.Permissions.ClaimType)
                        .Select(c => c.Value)
                        .Intersect(global::MikuTest.Web.Services.Permissions.Grantable)
                );
            else
            {
                var manager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                var admin = await manager.FindByNameAsync(AppRoles.Administrator);
                if (admin is not null)
                    permissions.UnionWith(
                        (await manager.GetClaimsAsync(admin))
                            .Where(c => c.Type == global::MikuTest.Web.Services.Permissions.ClaimType)
                            .Select(c => c.Value)
                            .Intersect(global::MikuTest.Web.Services.Permissions.Grantable)
                    );
            }
        }
        return new(new(user.Id, user.UserName ?? user.Id), role, permissions);
    }
}

public sealed class PermissionHandler(PermissionResolver resolver)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement
    )
    {
        if ((await resolver.ResolveAsync(context.User))?.Allows(requirement.Permission) == true)
            context.Succeed(requirement);
    }
}

public interface IManagementAccess
{
    Task<ManagementActor> RequireAsync(string permission);
    Task<bool> CanAsync(string permission);
}

public sealed class ManagementAccess(
    AuthenticationStateProvider authentication,
    PermissionResolver resolver,
    IHttpContextAccessor http
) : IManagementAccess
{
    public async Task<bool> CanAsync(string permission) =>
        (await resolver.ResolveAsync((await authentication.GetAuthenticationStateAsync()).User))?.Allows(
            permission
        ) == true;

    public async Task<ManagementActor> RequireAsync(string permission)
    {
        var snapshot = await resolver.ResolveAsync((await authentication.GetAuthenticationStateAsync()).User);
        if (snapshot?.Allows(permission) != true)
            throw new InvalidOperationException("当前账号没有此操作权限，或权限已变更。请重新登录。");
        return snapshot.Actor with { IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString() };
    }
}
