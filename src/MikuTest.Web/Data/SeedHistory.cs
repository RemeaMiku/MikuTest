using Microsoft.EntityFrameworkCore;

namespace MikuTest.Web.Data;

// Remember completed sample imports even if administrators later delete their quizzes.
public static class SeedHistory
{
    public static async Task OnceAsync(QuizDbContext db, string name, Func<Task> seed)
    {
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS SeedHistory (Name TEXT NOT NULL PRIMARY KEY)"
        );
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (
            await db
                .Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM SeedHistory WHERE Name = {name}")
                .SingleAsync() == 0
        )
        {
            await seed();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SeedHistory (Name) VALUES ({name})");
        }
        await transaction.CommitAsync();
    }
}
