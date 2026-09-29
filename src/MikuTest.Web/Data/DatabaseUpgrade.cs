using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Models;

namespace MikuTest.Web.Data;

public static class DatabaseUpgrade
{
    public static async Task ApplyAsync(QuizDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        if (!await HasColumn(db, "Questions", "Difficulty"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Questions ADD COLUMN Difficulty INTEGER NOT NULL DEFAULT 2"
            );
        if (!await HasColumn(db, "Attempts", "UserId"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Attempts ADD COLUMN UserId TEXT NULL");
        if (!await HasColumn(db, "Attempts", "IsRandom"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Attempts ADD COLUMN IsRandom INTEGER NOT NULL DEFAULT 0"
            );
        if (!await HasColumn(db, "Quizzes", "IsDeleted"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Quizzes ADD COLUMN IsDeleted INTEGER NOT NULL DEFAULT 0"
            );
        if (!await HasColumn(db, "Quizzes", "IsPublished"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Quizzes ADD COLUMN IsPublished INTEGER NOT NULL DEFAULT 0"
            );
            await db.Database.ExecuteSqlRawAsync("UPDATE Quizzes SET IsPublished = 1 WHERE IsDeleted = 0");
        }
        var hasAnswers = await HasTable(db, "Answers");
        var needsAnswerSnapshotBackfill = false;
        if (hasAnswers && !await HasColumn(db, "Answers", "OptionsJson"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Answers ADD COLUMN OptionsJson TEXT NOT NULL DEFAULT '[]'"
            );
            needsAnswerSnapshotBackfill = true;
        }
        if (hasAnswers && !await HasColumn(db, "Answers", "MediaJson"))
        {
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Answers ADD COLUMN MediaJson TEXT NOT NULL DEFAULT '[]'"
            );
            needsAnswerSnapshotBackfill = true;
        }
        var legacyQuestionOwnership = await HasColumn(db, "Questions", "QuizId");
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS Tags (
              Id INTEGER NOT NULL CONSTRAINT PK_Tags PRIMARY KEY AUTOINCREMENT,
              Name TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Tags_Name ON Tags(Name);
            CREATE TABLE IF NOT EXISTS QuestionTags (
              QuestionsId INTEGER NOT NULL,
              TagsId INTEGER NOT NULL,
              CONSTRAINT PK_QuestionTags PRIMARY KEY (QuestionsId, TagsId),
              CONSTRAINT FK_QuestionTags_Questions_QuestionsId FOREIGN KEY (QuestionsId) REFERENCES Questions(Id) ON DELETE CASCADE,
              CONSTRAINT FK_QuestionTags_Tags_TagsId FOREIGN KEY (TagsId) REFERENCES Tags(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_QuestionTags_TagsId ON QuestionTags(TagsId);
            CREATE TABLE IF NOT EXISTS KnowledgeDomains (
              Id INTEGER NOT NULL CONSTRAINT PK_KnowledgeDomains PRIMARY KEY AUTOINCREMENT,
              Name TEXT NOT NULL,
              "Order" INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_KnowledgeDomains_Name ON KnowledgeDomains(Name);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_KnowledgeDomains_Order ON KnowledgeDomains("Order");
            CREATE TABLE IF NOT EXISTS QuestionDomains (
              DomainsId INTEGER NOT NULL,
              QuestionsId INTEGER NOT NULL,
              CONSTRAINT PK_QuestionDomains PRIMARY KEY (DomainsId, QuestionsId),
              CONSTRAINT FK_QuestionDomains_KnowledgeDomains_DomainsId FOREIGN KEY (DomainsId) REFERENCES KnowledgeDomains(Id) ON DELETE CASCADE,
              CONSTRAINT FK_QuestionDomains_Questions_QuestionsId FOREIGN KEY (QuestionsId) REFERENCES Questions(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_QuestionDomains_QuestionsId ON QuestionDomains(QuestionsId);
            CREATE TABLE IF NOT EXISTS RandomPapers (
              Id TEXT NOT NULL CONSTRAINT PK_RandomPapers PRIMARY KEY,
              UserId TEXT NULL,
              CreatedAtUtc TEXT NOT NULL,
              Title TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS RandomPaperQuestions (
              RandomPaperId TEXT NOT NULL,
              QuestionId INTEGER NOT NULL,
              "Order" INTEGER NOT NULL,
              CONSTRAINT PK_RandomPaperQuestions PRIMARY KEY (RandomPaperId, QuestionId),
              CONSTRAINT FK_RandomPaperQuestions_RandomPapers_RandomPaperId FOREIGN KEY (RandomPaperId) REFERENCES RandomPapers(Id) ON DELETE CASCADE,
              CONSTRAINT FK_RandomPaperQuestions_Questions_QuestionId FOREIGN KEY (QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_RandomPaperQuestions_QuestionId ON RandomPaperQuestions(QuestionId);
            CREATE INDEX IF NOT EXISTS IX_Attempts_UserId_SubmittedAtUtc ON Attempts(UserId, SubmittedAtUtc);
            """
        );
        if (legacyQuestionOwnership)
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE QuizQuestions_New (
                  QuizId INTEGER NOT NULL,
                  QuestionId INTEGER NOT NULL,
                  "Order" INTEGER NOT NULL,
                  CONSTRAINT PK_QuizQuestions PRIMARY KEY (QuizId, QuestionId),
                  CONSTRAINT FK_QuizQuestions_Quizzes_QuizId FOREIGN KEY (QuizId) REFERENCES Quizzes(Id) ON DELETE CASCADE,
                  CONSTRAINT FK_QuizQuestions_Questions_QuestionId FOREIGN KEY (QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE);
                INSERT INTO QuizQuestions_New (QuizId, QuestionId, "Order")
                  SELECT QuizId, Id, "Order" FROM Questions;

                CREATE TABLE Questions_New (
                  Id INTEGER NOT NULL CONSTRAINT PK_Questions PRIMARY KEY AUTOINCREMENT,
                  Text TEXT NOT NULL,
                  Explanation TEXT NOT NULL,
                  Difficulty INTEGER NOT NULL);
                INSERT INTO Questions_New (Id, Text, Explanation, Difficulty)
                  SELECT Id, Text, Explanation, Difficulty FROM Questions;
                DROP TABLE Questions;
                ALTER TABLE Questions_New RENAME TO Questions;

                CREATE TABLE Attempts_New (
                  Id TEXT NOT NULL CONSTRAINT PK_Attempts PRIMARY KEY,
                  QuizId INTEGER NULL,
                  QuizTitle TEXT NOT NULL,
                  SubmittedAtUtc TEXT NOT NULL,
                  UserId TEXT NULL,
                  IsRandom INTEGER NOT NULL,
                  Score INTEGER NOT NULL,
                  Total INTEGER NOT NULL,
                  CONSTRAINT FK_Attempts_Quizzes_QuizId FOREIGN KEY (QuizId) REFERENCES Quizzes(Id) ON DELETE RESTRICT);
                INSERT INTO Attempts_New (Id, QuizId, QuizTitle, SubmittedAtUtc, UserId, IsRandom, Score, Total)
                  SELECT Id, QuizId, QuizTitle, SubmittedAtUtc, UserId, IsRandom, Score, Total FROM Attempts;
                DROP TABLE Attempts;
                ALTER TABLE Attempts_New RENAME TO Attempts;

                ALTER TABLE QuizQuestions_New RENAME TO QuizQuestions;
                CREATE UNIQUE INDEX IX_QuizQuestions_QuizId_Order ON QuizQuestions(QuizId, "Order");
                CREATE INDEX IX_QuizQuestions_QuestionId ON QuizQuestions(QuestionId);
                CREATE INDEX IX_Attempts_UserId_SubmittedAtUtc ON Attempts(UserId, SubmittedAtUtc);
                """
            );
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS QuizQuestions (
                  QuizId INTEGER NOT NULL,
                  QuestionId INTEGER NOT NULL,
                  "Order" INTEGER NOT NULL,
                  CONSTRAINT PK_QuizQuestions PRIMARY KEY (QuizId, QuestionId),
                  CONSTRAINT FK_QuizQuestions_Quizzes_QuizId FOREIGN KEY (QuizId) REFERENCES Quizzes(Id) ON DELETE CASCADE,
                  CONSTRAINT FK_QuizQuestions_Questions_QuestionId FOREIGN KEY (QuestionId) REFERENCES Questions(Id) ON DELETE CASCADE);
                CREATE UNIQUE INDEX IF NOT EXISTS IX_QuizQuestions_QuizId_Order ON QuizQuestions(QuizId, "Order");
                CREATE INDEX IF NOT EXISTS IX_QuizQuestions_QuestionId ON QuizQuestions(QuestionId);
                """
            );
        }
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS QuestionGroups (
              Id INTEGER NOT NULL CONSTRAINT PK_QuestionGroups PRIMARY KEY AUTOINCREMENT,
              Title TEXT NOT NULL, Content TEXT NOT NULL, MediaJson TEXT NOT NULL);
            """
        );
        if (!await HasColumn(db, "Questions", "GroupId"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Questions ADD COLUMN GroupId INTEGER NULL REFERENCES QuestionGroups(Id) ON DELETE RESTRICT"
            );
        if (!await HasColumn(db, "Questions", "GroupOrder"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Questions ADD COLUMN GroupOrder INTEGER NOT NULL DEFAULT 0"
            );
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_Questions_GroupId ON Questions(GroupId)"
        );
        if (hasAnswers && !await HasColumn(db, "Answers", "GroupId"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Answers ADD COLUMN GroupId INTEGER NULL");
        if (hasAnswers && !await HasColumn(db, "Answers", "GroupTitle"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Answers ADD COLUMN GroupTitle TEXT NOT NULL DEFAULT ''"
            );
        if (hasAnswers && !await HasColumn(db, "Answers", "GroupContent"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Answers ADD COLUMN GroupContent TEXT NOT NULL DEFAULT ''"
            );
        if (hasAnswers && !await HasColumn(db, "Answers", "GroupMediaJson"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Answers ADD COLUMN GroupMediaJson TEXT NOT NULL DEFAULT '[]'"
            );
        if (!await HasColumn(db, "QuizQuestions", "Points"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE QuizQuestions ADD COLUMN Points TEXT NOT NULL DEFAULT '1.0'"
            );
        if (hasAnswers && !await HasColumn(db, "Answers", "Points"))
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE Answers ADD COLUMN Points TEXT NOT NULL DEFAULT '1.0'"
            );
        if (await HasTable(db, "Choices") && !await HasColumn(db, "Choices", "ImageUrl"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Choices ADD COLUMN ImageUrl TEXT NULL");
        // New preset scores are whole points; submitted answer snapshots remain unchanged.
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE QuizQuestions SET Points = MAX(1, MIN(1000, ROUND(CAST(Points AS REAL)))) WHERE CAST(Points AS REAL) != CAST(Points AS INTEGER) OR CAST(Points AS REAL) < 1 OR CAST(Points AS REAL) > 1000"
        );
        await ManagementSchema.EnsureAuditAsync(db);
        await EnsureForeignKeysAsync(db);
        if (hasAnswers && needsAnswerSnapshotBackfill)
            await BackfillAnswerSnapshotsAsync(db);
    }

    private static async Task BackfillAnswerSnapshotsAsync(QuizDbContext db)
    {
        var answers = await db.Answers.Where(a => a.OptionsJson == "[]" || a.MediaJson == "[]").ToListAsync();
        if (answers.Count == 0)
            return;
        var ids = answers.Select(a => a.QuestionId).Distinct().ToArray();
        var questions = await db
            .Questions.AsNoTracking()
            .Include(q => q.Choices)
            .Include(q => q.Media)
            .Where(q => ids.Contains(q.Id))
            .ToDictionaryAsync(q => q.Id);
        foreach (var answer in answers)
        {
            if (!questions.TryGetValue(answer.QuestionId, out var question))
                continue;
            if (answer.OptionsJson == "[]")
                answer.OptionsJson = JsonSerializer.Serialize(
                    question.Choices.Select(c => new AnswerOptionSnapshot(
                        c.Text,
                        c.Id == answer.ChoiceId,
                        c.IsCorrect,
                        c.ImageUrl
                    ))
                );
            if (answer.MediaJson == "[]")
                answer.MediaJson = JsonSerializer.Serialize(
                    question.Media.Select(m => new AnswerMediaSnapshot(m.Kind, m.Url, m.Description))
                );
        }
        await db.SaveChangesAsync();
    }

    private static async Task<bool> HasTable(QuizDbContext db, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        await db.Database.OpenConnectionAsync();
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    private static async Task<bool> HasColumn(QuizDbContext db, string table, string column)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static async Task EnsureForeignKeysAsync(QuizDbContext db)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check";
        await db.Database.OpenConnectionAsync();
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
            throw new InvalidOperationException(
                $"数据库外键检查失败：{reader.GetString(0)} 第 {reader.GetValue(1)} 行。"
            );
    }
}
