using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MikuTest.Web.Data;
using MikuTest.Web.Models;
using MikuTest.Web.Services;

var path = Path.Combine(Path.GetTempPath(), $"mikutest-{Guid.NewGuid()}.db");
var legacyPath = Path.Combine(Path.GetTempPath(), $"mikutest-legacy-{Guid.NewGuid()}.db");
var identityPath = Path.Combine(Path.GetTempPath(), $"mikutest-identity-{Guid.NewGuid()}.db");
var options = new DbContextOptionsBuilder<QuizDbContext>()
    .UseSqlite($"Data Source={path};Pooling=False")
    .Options;
try
{
    await GroupChecks.Run();
    await ScoringChecks.Run();
    await UploadChecks.Run();
    TestBankFilters();
    await using (var db = new QuizDbContext(options))
    {
        await DatabaseUpgrade.ApplyAsync(db);
        await SeedData.InitializeAsync(db);
        await SeedData.InitializeAsync(db);
        await Phase2Seed.InitializeAsync(db);
        await DevelopmentQuestionSeed.InitializeAsync(db);
        await DevelopmentQuestionSeed.InitializeAsync(db);
        await KnowledgeDomainSeed.InitializeAsync(db);
        await KnowledgeDomainSeed.InitializeAsync(db);
        Check(await db.Quizzes.CountAsync() == 2, "seed creates two quizzes once");
        Check(await db.Questions.CountAsync() == 28, "seed creates independent question bank");
        Check(await db.QuizQuestions.CountAsync() == 28, "seed creates preset memberships");
        Check(await db.KnowledgeDomains.CountAsync() == 7, "seed creates seven curriculum domains once");
        Check(
            !await db.Tags.AnyAsync(t =>
                new[] { "基础知识", "角色", "软件版本", "历史", "声库", "文化", "活动" }.Contains(t.Name)
            ),
            "obsolete content tags are removed"
        );
        Check(
            await db.Questions.AllAsync(q => q.Domains.Any()),
            "seeded questions receive curriculum domains"
        );
    }
    var factory = new Factory(options);
    var quizzes = new QuizService(factory, new TestManagementAccess());
    var questions = new QuestionService(factory, new TestManagementAccess());
    var admin = new QuizAdminService(factory, new TestManagementAccess());
    var domainIds = (await questions.DomainsAsync()).Take(2).Select(d => d.Id).ToArray();
    var initial = (await quizzes.ListAsync()).First();
    Check(initial.Questions.Count == 4, "preset loads through join table");
    var standaloneId = await questions.SaveAsync(
        new Question
        {
            Text = "Standalone",
            Explanation = "Independent",
            Difficulty = Difficulty.Normal,
            Choices = [new() { Text = "A", IsCorrect = true }, new() { Text = "B" }],
            Media =
            [
                new()
                {
                    Kind = MediaKind.Image,
                    Url = "/media/card.svg",
                    Description = "Snapshot media",
                },
            ],
        },
        ["only-standalone"],
        domainIds
    );
    Check(
        (await questions.ListAsync()).Single(q => q.Id == standaloneId).Domains.Count == 2,
        "question supports multiple curriculum domains"
    );
    var tagDraft = (await questions.ListAsync()).Single(q => q.Id == standaloneId);
    var tagId = (await questions.TagsAsync()).Single(t => t.Name == "only-standalone").Id;
    await questions.SaveAsync(tagDraft, [" ONLY-STANDALONE ", "new-picker-tag"], domainIds);
    var tagged = (await questions.ListAsync()).Single(q => q.Id == standaloneId);
    Check(
        tagged.Tags.Count == 2 && tagged.Tags.Any(t => t.Id == tagId),
        "tag picker save reuses existing tag case insensitively and adds new tag"
    );
    await questions.SaveAsync(tagged, [], domainIds);
    Check(
        (await questions.ListAsync()).Single(q => q.Id == standaloneId).Tags.Count == 0
            && (await questions.TagsAsync()).Any(t => t.Name == "new-picker-tag"),
        "deselecting tags removes only membership and leaves tags available"
    );
    await Reject(
        () => questions.SaveAsync(tagged, ["bad,tag"], domainIds),
        "tag save rejects comma-separated input"
    );
    await Reject(
        () => questions.SaveAsync(tagged, Enumerable.Range(1, 11).Select(i => $"excess-{i}"), domainIds),
        "tag save enforces ten tag limit"
    );
    Check(
        !(await questions.TagsAsync()).Any(t => t.Name.StartsWith("excess-")),
        "invalid tag save leaves no new tags"
    );
    await using (var db = new QuizDbContext(options))
        Check(
            !await db.QuizQuestions.AnyAsync(i => i.QuestionId == standaloneId),
            "new question can remain unassigned"
        );
    var quizId = await admin.SaveAsync(new Quiz { Title = "Reusable set", Description = "composition test" });
    Check(
        await quizzes.GetAsync(quizId) is null && !(await quizzes.ListAsync()).Any(q => q.Id == quizId),
        "new quiz remains private draft"
    );
    Check(
        (await quizzes.GetForAdminAsync(quizId)) is { IsPublished: false, IsDeleted: false },
        "admin can compose draft"
    );
    await Reject(() => admin.PublishAsync(quizId), "empty draft cannot publish");
    await Reject(
        () => quizzes.SubmitAsync(quizId, new Dictionary<int, int>()),
        "draft cannot accept direct submissions"
    );
    await admin.AddQuestionAsync(quizId, standaloneId);
    await admin.UnpublishAsync(initial.Id);
    await admin.AddQuestionAsync(initial.Id, standaloneId);
    await using (var db = new QuizDbContext(options))
        Check(
            await db.QuizQuestions.CountAsync(i => i.QuestionId == standaloneId) == 2,
            "one question belongs to multiple quizzes"
        );
    var another = (await questions.ListAsync()).First(q => q.Id != standaloneId);
    await admin.AddQuestionAsync(quizId, another.Id);
    await Reject(() => admin.AddQuestionAsync(quizId, standaloneId), "reject duplicate membership");
    await admin.PlaceAsync(quizId, another.Id, null, false);
    var composed = (await quizzes.GetForAdminAsync(quizId))!;
    Check(
        composed.Questions.Select(q => q.Id).SequenceEqual(new[] { another.Id, standaloneId }),
        "preset order is independent and persistent"
    );
    await admin.RemoveQuestionAsync(quizId, standaloneId);
    Check(
        await questions.ListAsync() is { } bank && bank.Any(q => q.Id == standaloneId),
        "removing from preset keeps question"
    );
    await using (var db = new QuizDbContext(options))
        Check(
            await db.QuizQuestions.CountAsync(i => i.QuestionId == standaloneId) == 1,
            "removal affects only one preset"
        );
    await admin.PublishAsync(quizId);
    composed = (await quizzes.GetAsync(quizId))!;
    var correct = composed.Questions.ToDictionary(q => q.Id, q => q.Choices.Single(c => c.IsCorrect).Id);
    var attemptId = await quizzes.SubmitAsync(quizId, correct, "user-a");
    Check(
        (await quizzes.ResultAsync(attemptId, "user-a"))?.Score == 1,
        "fixed preset scores through memberships"
    );
    var anonymousAttempt = await quizzes.SubmitAsync(quizId, correct);
    var otherUserAttempt = await quizzes.SubmitAsync(quizId, correct, "user-b");
    await admin.UnpublishAsync(quizId);
    Check(
        await quizzes.GetAsync(quizId) is null && !(await quizzes.ListAsync()).Any(q => q.Id == quizId),
        "archived quiz rejects new access and leaves public list"
    );
    Check(
        (await quizzes.ResultAsync(attemptId, "user-a")) is { CanRetry: false },
        "archived quiz history remains readable without retry"
    );
    Check(
        (await quizzes.AdminListAsync()).Single(q => q.Id == quizId).IsDeleted,
        "archived quiz remains in admin list"
    );
    await admin.AddQuestionAsync(quizId, standaloneId);
    Check(
        (await quizzes.GetForAdminAsync(quizId))!.Questions.Any(q => q.Id == standaloneId),
        "archived quiz remains editable"
    );
    await Reject(
        () => quizzes.SubmitAsync(quizId, correct, "user-a"),
        "archived quiz rejects new submissions"
    );
    await admin.PublishAsync(quizId);
    Check((await quizzes.GetAsync(quizId)) is not null, "archived quiz can be restored");
    await admin.UnpublishAsync(quizId);
    await admin.SaveAsync(
        new Quiz
        {
            Id = quizId,
            Title = "Reusable set renamed",
            Description = "updated",
        }
    );
    Check(
        (await quizzes.GetForAdminAsync(quizId))?.Title.EndsWith("renamed") == true,
        "quiz metadata updates"
    );
    var emptyId = await admin.SaveAsync(new Quiz { Title = "Temporary empty", Description = "delete me" });
    await admin.DeleteAsync(emptyId);
    Check(await quizzes.GetAsync(emptyId) is null, "empty quiz deletes without deleting questions");
    await admin.PublishAsync(initial.Id);
    var sharedAttempt = (await quizzes.GetAsync(initial.Id))!;
    var sharedAnswers = sharedAttempt.Questions.ToDictionary(
        q => q.Id,
        q => q.Choices.Single(c => c.IsCorrect).Id
    );
    var historyId = await quizzes.SubmitAsync(initial.Id, sharedAnswers);
    await admin.UnpublishAsync(initial.Id);
    await questions.DeleteAsync(standaloneId);
    await using (var db = new QuizDbContext(options))
        Check(
            !await db.QuizQuestions.AnyAsync(i => i.QuestionId == standaloneId),
            "question deletion removes all preset memberships"
        );
    var preserved = (await quizzes.ResultAsync(historyId))?.Answers.SingleOrDefault(a =>
        a.QuestionId == standaloneId
    );
    Check(preserved is not null, "question deletion preserves history snapshots");
    Check(
        preserved!.Options.Count == 2
            && preserved.Options.Any(o => o.IsSelected && o.IsCorrect && o.Text == "A"),
        "history snapshot preserves every option and answer state"
    );
    Check(
        preserved.Media.Count == 1 && preserved.Media[0].Url == "/media/card.svg",
        "history snapshot preserves media after question deletion"
    );
    Check(preserved.QuestionDeleted, "history marks questions removed from the question bank");
    var randomId = await questions.SaveAsync(
        new Question
        {
            Text = "Random unassigned",
            Choices = [new() { Text = "yes", IsCorrect = true }, new() { Text = "no" }],
        },
        ["random-unassigned"],
        [domainIds[1]]
    );
    var random = new RandomQuizService(factory);
    var tag = (await random.TagsAsync()).Single(t => t.Name == "random-unassigned");
    var paperId = await random.CreateAsync(new(1, null, [tag.Id], [domainIds[1]]), "user-r");
    var paper = (await random.GetAsync(paperId, "user-r"))!.Value;
    Check(paper.Questions.Single().Id == randomId, "random quiz includes unassigned questions");
    var randomAttempt = await random.SubmitAsync(
        paperId,
        new Dictionary<int, int>
        {
            { randomId, paper.Questions.Single().Choices.Single(c => c.IsCorrect).Id },
        },
        "user-r"
    );
    Check(
        (await quizzes.ResultAsync(randomAttempt, "user-r")) is { IsRandom: true, QuizId: null },
        "random result does not require a preset quiz"
    );
    await Reject(
        () => admin.SaveAsync(new Quiz { Title = " ", Description = "" }),
        "reject empty quiz title"
    );
    await Reject(
        () => admin.SaveAsync(new Quiz { Title = "Reusable set renamed" }),
        "reject duplicate quiz title"
    );
    await admin.DeleteAsync(quizId);
    Check(
        await quizzes.ResultAsync(attemptId, "user-a") is null
            && !(await quizzes.HistoryAsync("user-a")).Any(a => a.QuizId == quizId),
        "deletion removes results and personal history"
    );
    Check(
        await quizzes.ResultAsync(anonymousAttempt) is null
            && await quizzes.ResultAsync(otherUserAttempt, "user-b") is null,
        "deletion removes anonymous and other users' attempts too"
    );
    Check(
        !(await quizzes.AdminListAsync()).Any(q => q.Id == quizId),
        "deleted quiz disappears from admin list"
    );
    await Reject(() => quizzes.SubmitAsync(quizId, correct), "deleted quiz cannot accept stale submissions");
    await using (var db = new QuizDbContext(options))
    {
        Check(
            !await db.Answers.AnyAsync(a => a.AttemptId == attemptId)
                && !await db.QuizQuestions.AnyAsync(q => q.QuizId == quizId),
            "deletion removes answers and memberships"
        );
        Check(
            await db.Questions.AnyAsync(q => q.Id == another.Id),
            "deleting quiz preserves shared bank questions"
        );
    }
    Check(
        await quizzes.ResultAsync(randomAttempt, "user-r") is not null
            && await quizzes.ResultAsync(historyId) is not null,
        "other quiz and random history survive deletion"
    );
    foreach (var seeded in await quizzes.AdminListAsync())
        await admin.DeleteAsync(seeded.Id);
    await using (var db = new QuizDbContext(options))
    {
        await SeedData.InitializeAsync(db);
        await DevelopmentQuestionSeed.InitializeAsync(db);
        Check(!await db.Quizzes.AnyAsync(), "restart seeding does not recreate deleted sample quizzes");
    }
    await TestLegacyMigration(legacyPath);
    await PermissionChecks.Run(identityPath);
    Check(
        IsAdminOnly(typeof(MikuTest.Web.Components.Pages.AdminHome))
            && IsAdminOnly(typeof(MikuTest.Web.Components.Pages.AdminUsers))
            && IsAdminOnly(typeof(MikuTest.Web.Components.Pages.AdminQuestions))
            && IsAdminOnly(typeof(MikuTest.Web.Components.Pages.AdminQuizzes))
            && IsAdminOnly(typeof(MikuTest.Web.Components.Pages.AdminQuizComposition)),
        "all admin pages require permission policies"
    );
    Console.WriteLine("All smoke checks passed.");
}
finally
{
    foreach (var file in new[] { path, legacyPath, identityPath })
        if (File.Exists(file))
            File.Delete(file);
}

static void TestBankFilters()
{
    var bank = new List<Question>
    {
        new()
        {
            Id = 12,
            Text = "MIKU 图片",
            Difficulty = Difficulty.Hard,
            Domains = [new() { Id = 2 }],
            Tags = [new() { Id = 3 }],
            Media = [new() { Kind = MediaKind.Image }, new() { Kind = MediaKind.Audio }],
        },
        new()
        {
            Id = 2,
            Text = "包含 12 的文字题",
            Difficulty = Difficulty.Easy,
            Domains = [new() { Id = 1 }],
            Tags = [new() { Id = 4 }],
        },
        new()
        {
            Id = 30,
            Text = "视频题",
            Difficulty = Difficulty.Normal,
            Media = [new() { Kind = MediaKind.Video }],
        },
    };
    var filter = new BankFilter { Search = " #12 " };
    Check(
        filter.Apply(bank).Select(q => q.Id).SequenceEqual(new[] { 12 }),
        "hash ID search matches exact ID rather than text"
    );
    filter.Search = "12";
    Check(filter.Apply(bank).Count == 2, "numeric search matches ID or question text");
    filter.Search = "miku";
    filter.DomainId = 2;
    filter.TagId = 3;
    filter.Difficulty = Difficulty.Hard;
    filter.Type = "audio";
    Check(
        filter.Apply(bank).Single().Id == 12,
        "combined bank filters support case insensitive text and mixed media"
    );
    filter.DomainId = 1;
    Check(filter.Apply(bank).Count == 0, "bank filter categories intersect");
    filter.Reset();
    filter.Type = "text";
    Check(filter.Apply(bank).Single().Id == 2, "text type excludes all media questions");
    filter.Reset();
    filter.Descending = true;
    Check(
        filter.Apply(bank).Select(q => q.Id).SequenceEqual(new[] { 30, 12, 2 }),
        "bank ID sort is numeric descending"
    );
    Check(
        BankFilter.Matches(8, "Miku Test", "test") && !BankFilter.Matches(8, "Miku Test", "#9"),
        "quiz title and ID search share matching rules"
    );
}

static async Task TestLegacyMigration(string path)
{
    var options = new DbContextOptionsBuilder<QuizDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False")
        .Options;
    await using var db = new QuizDbContext(options);
    await db.Database.OpenConnectionAsync();
    await db.Database.ExecuteSqlRawAsync(
        """
        CREATE TABLE Quizzes(Id INTEGER PRIMARY KEY AUTOINCREMENT,Title TEXT NOT NULL,Description TEXT NOT NULL);
        CREATE TABLE Questions(Id INTEGER PRIMARY KEY AUTOINCREMENT,QuizId INTEGER NOT NULL,"Order" INTEGER NOT NULL,Text TEXT NOT NULL,Explanation TEXT NOT NULL);
        CREATE TABLE Attempts(Id TEXT PRIMARY KEY,QuizId INTEGER NOT NULL,QuizTitle TEXT NOT NULL,SubmittedAtUtc TEXT NOT NULL,Score INTEGER NOT NULL,Total INTEGER NOT NULL);
        INSERT INTO Quizzes(Id,Title,Description) VALUES(1,'Legacy','Old');INSERT INTO Questions(Id,QuizId,"Order",Text,Explanation) VALUES(1,1,1,'Old question','Old explanation');
        """
    );
    await DatabaseUpgrade.ApplyAsync(db);
    await DatabaseUpgrade.ApplyAsync(db);
    Check(
        await db.Quizzes.AnyAsync(q => q.Id == 1 && q.IsPublished),
        "migration preserves existing published quiz and is repeatable"
    );
    await KnowledgeDomainSeed.InitializeAsync(db);
    Check(
        await db.QuizQuestions.AnyAsync(i => i.QuizId == 1 && i.QuestionId == 1 && i.Order == 1),
        "legacy ownership migrates to join table"
    );
    db.Questions.Add(
        new Question
        {
            Text = "Unassigned after migration",
            Explanation = "",
            Difficulty = Difficulty.Normal,
        }
    );
    await db.SaveChangesAsync();
    Check(await db.Questions.CountAsync() == 2, "migrated database accepts unassigned questions");
    Check(await db.KnowledgeDomains.CountAsync() == 7, "legacy database receives curriculum domains");
}

static void Check(bool value, string name)
{
    if (!value)
        throw new Exception(name);
    Console.WriteLine($"PASS: {name}");
}
static async Task Reject(Func<Task> action, string name)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        Console.WriteLine($"PASS: {name}");
        return;
    }
    throw new Exception(name);
}
static bool IsAdminOnly(Type page) =>
    page.GetCustomAttributes(typeof(AuthorizeAttribute), true)
        .Cast<AuthorizeAttribute>()
        .Any(a => !string.IsNullOrEmpty(a.Policy));

sealed class Factory(DbContextOptions<QuizDbContext> options) : IDbContextFactory<QuizDbContext>
{
    public QuizDbContext CreateDbContext() => new(options);
}
