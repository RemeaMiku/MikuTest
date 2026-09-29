using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;

namespace MikuTest.Web.Services;

public static class ContentAudit
{
    public static object Changes(DbContext db)
    {
        db.ChangeTracker.DetectChanges();
        return db
            .ChangeTracker.Entries()
            .Where(e =>
                e.Entity is not AuditLog
                && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            )
            .Select(e => new
            {
                Entity = e.Metadata.ClrType.Name,
                State = e.State.ToString(),
                Before = e.State == EntityState.Added
                    ? null
                    : e.OriginalValues.Properties.ToDictionary(p => p.Name, p => e.OriginalValues[p]),
                After = e.State == EntityState.Deleted
                    ? null
                    : e.CurrentValues.Properties.ToDictionary(p => p.Name, p => e.CurrentValues[p]),
            })
            .ToArray();
    }

    public static async Task SaveAsync(
        QuizDbContext db,
        ManagementActor actor,
        string action,
        string kind,
        Func<object> target
    )
    {
        // 调用方持有事务；业务数据与审计一起提交，并在保存后取得新记录 ID。
        var changes = Changes(db);
        await db.SaveChangesAsync();
        db.AuditLogs.Add(AuditLog.Create(actor, action, kind, target(), after: changes));
        await db.SaveChangesAsync();
    }
}

public sealed class ManagementRecords(
    IServiceScopeFactory scopes,
    IDbContextFactory<QuizDbContext> quizzes,
    IManagementAccess access
)
{
    public async Task<SiteConfiguration> SettingsAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        return await db.SiteConfigurations.AsNoTracking().SingleAsync(s => s.Id == 1);
    }

    public async Task SaveSettingsAsync(bool registrationEnabled, string announcement)
    {
        var actor = await access.RequireAsync(Permissions.SystemSettings);
        if (announcement.Length > 1000)
            throw new InvalidOperationException("公告最多 1000 字。");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync();
        var settings = await db.SiteConfigurations.SingleAsync(s => s.Id == 1);
        var before = new { settings.RegistrationEnabled, settings.Announcement };
        settings.RegistrationEnabled = registrationEnabled;
        settings.Announcement = announcement;
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                "System.Settings",
                "Configuration",
                1,
                before,
                new { registrationEnabled, announcement }
            )
        );
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    }

    public async Task<List<AuditLog>> AuditAsync(string search)
    {
        await access.RequireAsync(Permissions.AuditView);
        search = search.Trim();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        await using var quizDb = await quizzes.CreateDbContextAsync();
        IQueryable<AuditLog> Filter(IQueryable<AuditLog> q) =>
            string.IsNullOrEmpty(search)
                ? q
                : q.Where(a =>
                    a.ActorName.Contains(search)
                    || a.ActorUserId == search
                    || a.Action.Contains(search)
                    || a.TargetId == search
                    || a.TargetType.Contains(search)
                );
        var identity = await Filter(db.AuditLogs.AsNoTracking())
            .OrderByDescending(a => a.Timestamp)
            .Take(200)
            .ToListAsync();
        var content = await Filter(quizDb.AuditLogs.AsNoTracking())
            .OrderByDescending(a => a.Timestamp)
            .Take(200)
            .ToListAsync();
        return identity.Concat(content).OrderByDescending(a => a.Timestamp).Take(200).ToList();
    }
}
