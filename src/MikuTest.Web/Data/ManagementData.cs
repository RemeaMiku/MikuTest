using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Services;

namespace MikuTest.Web.Data;

public enum AccountStatus
{
    Active,
    Suspended,
    Deleted,
}

public sealed class AuditLog
{
    public long Id { get; set; }
    public string ActorUserId { get; set; } = "";
    public string ActorName { get; set; } = "";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string? IpAddress { get; set; }
    public string Details { get; set; } = "";

    public static AuditLog Create(
        ManagementActor actor,
        string action,
        string targetType,
        object targetId,
        object? before = null,
        object? after = null
    ) =>
        new()
        {
            ActorUserId = actor.Id,
            ActorName = actor.Name,
            IpAddress = actor.IpAddress,
            Action = action,
            TargetType = targetType,
            TargetId = targetId.ToString()!,
            Details = JsonSerializer.Serialize(new { Before = before, After = after }),
        };
}

public sealed class SiteConfiguration
{
    public int Id { get; set; } = 1;
    public bool RegistrationEnabled { get; set; } = true;
    public string Announcement { get; set; } = "";
}

public static class ManagementSchema
{
    public static Task EnsureAuditAsync(DbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS AuditLogs (Id INTEGER PRIMARY KEY AUTOINCREMENT, ActorUserId TEXT NOT NULL, ActorName TEXT NOT NULL, Action TEXT NOT NULL, TargetType TEXT NOT NULL, TargetId TEXT NOT NULL, Timestamp TEXT NOT NULL, IpAddress TEXT NULL, Details TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_AuditLogs_Timestamp ON AuditLogs(Timestamp);
            """
        );

    public static async Task UpgradeIdentityAsync(IdentityDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.OpenConnectionAsync();
        var columns = new HashSet<string>();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(AspNetUsers)";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("Status"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE AspNetUsers ADD COLUMN Status INTEGER NOT NULL DEFAULT 0"
            );
        if (!columns.Contains("PermissionsConfigured"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE AspNetUsers ADD COLUMN PermissionsConfigured INTEGER NOT NULL DEFAULT 0"
            );
        await EnsureAuditAsync(db);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS SiteConfigurations (Id INTEGER PRIMARY KEY, RegistrationEnabled INTEGER NOT NULL, Announcement TEXT NOT NULL)"
        );
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO SiteConfigurations(Id,RegistrationEnabled,Announcement) VALUES(1,1,'')"
        );
    }
}
