using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MikuTest.Web.Data;
using MikuTest.Web.Models;
using MikuTest.Web.Services;

static class PermissionChecks
{
    public static async Task Run(string path)
    {
        var quizPath = path + "-quiz";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddDbContext<IdentityDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
        services.AddDbContextFactory<QuizDbContext>(o =>
            o.UseSqlite($"Data Source={quizPath};Pooling=False")
        );
        services
            .AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole>()
            .AddSignInManager<ActiveAccountSignInManager>()
            .AddEntityFrameworkStores<IdentityDbContext>();
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddAuthorization(Permissions.Configure);
        services.AddScoped<PermissionResolver>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        var auth = new TestAuthentication();
        services.AddSingleton<AuthenticationStateProvider>(auth);
        services.AddScoped<IManagementAccess, ManagementAccess>();
        services.AddScoped<AccountAdminService>();
        services.AddScoped<QuestionService>();
        services.AddScoped<QuizAdminService>();
        services.AddScoped<ManagementRecords>();
        await using var provider = services.BuildServiceProvider();
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { { "SuperAdminAccount:ExistingUserName", "owner" } }
                )
                .Build();
            string ownerId,
                adminId,
                userId,
                otherId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
                await db.Database.EnsureCreatedAsync();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                Ensure(await roles.CreateAsync(new("Administrator")));
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var owner = new ApplicationUser { UserName = "owner", DisplayName = "Owner" };
                Ensure(await users.CreateAsync(owner, "TestOnly#2026"));
                Ensure(await users.AddToRoleAsync(owner, "Administrator"));
                ownerId = owner.Id;
                await IdentitySeed.InitializeAsync(scope.ServiceProvider, config);
                await IdentitySeed.InitializeAsync(scope.ServiceProvider, config);
                Check(
                    await users.IsInRoleAsync(owner, AppRoles.SuperAdmin)
                        && await users.IsInRoleAsync(owner, AppRoles.Administrator)
                        && await users.CheckPasswordAsync(owner, "TestOnly#2026"),
                    "legacy role and owner migrate without password reset"
                );
                Check(
                    await db.AuditLogs.CountAsync(a => a.Action == "SuperAdmin.Bootstrap") == 1,
                    "owner bootstrap is idempotent"
                );
                async Task<string> Add(string name)
                {
                    var u = new ApplicationUser { UserName = name, DisplayName = name };
                    Ensure(await users.CreateAsync(u, "TestOnly#2026"));
                    Ensure(await users.AddToRoleAsync(u, AppRoles.User));
                    return u.Id;
                }
                adminId = await Add("editor");
                userId = await Add("learner");
                otherId = await Add("another");
                await using var quiz = await scope
                    .ServiceProvider.GetRequiredService<IDbContextFactory<QuizDbContext>>()
                    .CreateDbContextAsync();
                await DatabaseUpgrade.ApplyAsync(quiz);
                quiz.KnowledgeDomains.Add(new() { Name = "Test domain" });
                quiz.Attempts.Add(
                    new()
                    {
                        Id = Guid.NewGuid(),
                        UserId = userId,
                        QuizTitle = "Preserved history",
                        IsRandom = true,
                    }
                );
                await quiz.SaveChangesAsync();
            }
            async Task Login(string id)
            {
                await using var scope = provider.CreateAsyncScope();
                var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var u = (await users.FindByIdAsync(id))!;
                auth.Principal = await scope
                    .ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>()
                    .CreateAsync(u);
            }
            await using var actions = provider.CreateAsyncScope();
            var sp = actions.ServiceProvider;
            var accounts = sp.GetRequiredService<AccountAdminService>();
            var access = sp.GetRequiredService<IManagementAccess>();
            var questions = sp.GetRequiredService<QuestionService>();
            var records = sp.GetRequiredService<ManagementRecords>();
            await Reject(() => accounts.ListAsync(), "anonymous cannot list accounts");
            await Login(userId);
            await Reject(() => questions.ListAsync(), "ordinary user cannot read answer bank");
            await Reject(() => accounts.SetAdminAsync(otherId, true), "ordinary user cannot appoint admin");
            await Login(ownerId);
            await accounts.SetAdminAsync(adminId, true);
            await Reject(() => accounts.SetAdminAsync(ownerId, false), "cannot demote owner");
            await Reject(
                () => accounts.SetStatusAsync(ownerId, AccountStatus.Deleted),
                "cannot delete self or owner"
            );
            await Login(adminId);
            Check(
                await access.CanAsync(Permissions.QuestionCreate)
                    && !await access.CanAsync(Permissions.UserDelete)
                    && !await access.CanAsync(Permissions.AdminManage),
                "default admin has content permissions but no account deletion or role management"
            );
            await Reject(
                () => accounts.SetAdminAsync(otherId, true),
                "admin cannot escalate another account"
            );
            await Reject(
                () => accounts.SetStatusAsync(userId, AccountStatus.Deleted),
                "default admin cannot soft delete"
            );
            await Reject(
                () => accounts.SetStatusAsync(ownerId, AccountStatus.Suspended),
                "admin cannot suspend superadmin"
            );
            await Reject(() => records.AuditAsync(""), "admin cannot see system audit");
            Check(
                (await accounts.ListAsync()).All(a => a.Role == AppRoles.User),
                "ordinary account list excludes administrators"
            );
            await accounts.SetStatusAsync(userId, AccountStatus.Suspended);
            await Login(userId);
            Check(!await access.CanAsync(Permissions.AdminAccess), "suspended account loses access");
            await using (var s = provider.CreateAsyncScope())
            {
                var u = await s
                    .ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>()
                    .FindByIdAsync(userId);
                Check(
                    !await s
                        .ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>()
                        .CanSignInAsync(u!),
                    "suspended user cannot sign in"
                );
            }
            await Login(adminId);
            await accounts.SetStatusAsync(userId, AccountStatus.Active);
            var oldAdmin = auth.Principal;
            await Login(ownerId);
            await accounts.SetPermissionsAsync(adminId, [Permissions.QuestionView]);
            auth.Principal = oldAdmin;
            Check(
                !await access.CanAsync(Permissions.QuestionView),
                "permission change invalidates old login stamp"
            );
            await Login(adminId);
            Check(
                await access.CanAsync(Permissions.QuestionView)
                    && !await access.CanAsync(Permissions.QuestionEdit),
                "explicit readonly permissions override role defaults"
            );
            Question Draft() =>
                new()
                {
                    Text = "Permission test",
                    Difficulty = Difficulty.Hard,
                    Choices = [new() { Text = "A", IsCorrect = true }, new() { Text = "B" }],
                };
            await Reject(
                () => questions.SaveAsync(Draft(), [], [1]),
                "readonly admin cannot directly call mutation service"
            );
            await Reject(
                () => sp.GetRequiredService<QuizAdminService>().SaveAsync(new() { Title = "Blocked" }),
                "readonly admin cannot create quiz"
            );
            await Login(ownerId);
            await Reject(
                () => accounts.SetPermissionsAsync(adminId, [Permissions.AdminManage]),
                "super-only permissions cannot be granted to admin"
            );
            await accounts.SetPermissionsAsync(adminId, [Permissions.QuestionCreate, Permissions.UserDelete]);
            await Login(adminId);
            Check(
                await access.CanAsync(Permissions.QuestionView)
                    && await access.CanAsync(Permissions.UserView),
                "dependent read permissions are included automatically"
            );
            var qid = await questions.SaveAsync(Draft(), [], [1]);
            await accounts.SetStatusAsync(userId, AccountStatus.Deleted);
            await using (var s = provider.CreateAsyncScope())
            {
                var users = s.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
                var u = (await users.FindByIdAsync(userId))!;
                Check(
                    u.Status == AccountStatus.Deleted
                        && !await s
                            .ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>()
                            .CanSignInAsync(u),
                    "soft deleted account remains but cannot sign in"
                );
                Check(
                    !(await users.CreateAsync(new() { UserName = u.UserName }, "TestOnly#2026")).Succeeded,
                    "soft deleted name cannot be reused"
                );
                await using var db = await s
                    .ServiceProvider.GetRequiredService<IDbContextFactory<QuizDbContext>>()
                    .CreateDbContextAsync();
                Check(
                    await db.Attempts.AnyAsync(a => a.UserId == userId),
                    "soft delete preserves personal answer history"
                );
                var audit = await db.AuditLogs.SingleAsync(a => a.Action == "Question.Create");
                Check(
                    audit.ActorUserId == adminId
                        && audit.TargetId == qid.ToString()
                        && audit.Details.Contains("IsCorrect")
                        && audit.Details.Contains("Difficulty"),
                    "content audit records actor, target, answer and difficulty changes"
                );
            }
            await accounts.SetStatusAsync(userId, AccountStatus.Active);
            await Login(ownerId);
            await accounts.SetAdminAsync(adminId, false);
            await Login(adminId);
            Check(
                !await access.CanAsync(Permissions.QuestionCreate)
                    && !await access.CanAsync(Permissions.AdminAccess),
                "demoted admin loses all management access"
            );
            await Login(ownerId);
            await records.SaveSettingsAsync(false, "Test announcement");
            Check(!(await records.SettingsAsync()).RegistrationEnabled, "registration setting persists");
            var logs = await records.AuditAsync("");
            Check(
                logs.Any(a => a.Action == "Admin.Permissions")
                    && logs.Any(a => a.Action == "User.SoftDelete")
                    && logs.Any(a => a.Action == "System.Settings")
                    && logs.Any(a => a.Action == "Question.Create"),
                "combined audit contains account, permission, settings and content operations"
            );
            Check(
                (
                    await sp.GetRequiredService<IAuthorizationService>()
                        .AuthorizeAsync(auth.Principal, null, Permissions.SuperAccess)
                ).Succeeded,
                "superadmin route policy succeeds"
            );
            await Login(userId);
            Check(
                !(
                    await sp.GetRequiredService<IAuthorizationService>()
                        .AuthorizeAsync(auth.Principal, null, Permissions.AdminAccess)
                ).Succeeded,
                "ordinary route policy denies access"
            );
        }
        finally
        {
            if (File.Exists(quizPath))
                File.Delete(quizPath);
        }
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
            throw new Exception(string.Join(";", result.Errors.Select(e => e.Description)));
    }

    private static void Check(bool ok, string name)
    {
        if (!ok)
            throw new Exception(name);
        Console.WriteLine("PASS: " + name);
    }

    private static async Task Reject(Func<Task> action, string name)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            Console.WriteLine("PASS: " + name);
            return;
        }
        throw new Exception(name);
    }

    private sealed class TestAuthentication : AuthenticationStateProvider
    {
        public ClaimsPrincipal Principal { get; set; } = new(new ClaimsIdentity());

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(Principal));
    }
}
