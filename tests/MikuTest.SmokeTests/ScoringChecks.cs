using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;
using MikuTest.Web.Models;
using MikuTest.Web.Services;

static class ScoringChecks
{
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mikutest-points-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<QuizDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            await using (var db = new QuizDbContext(options))
            {
                await DatabaseUpgrade.ApplyAsync(db);
                await KnowledgeDomainSeed.InitializeAsync(db);
            }
            var factory = new Factory(options);
            var admin = new QuizAdminService(factory, new TestManagementAccess());
            var quizzes = new QuizService(factory, new TestManagementAccess());
            var questions = new QuestionService(factory, new TestManagementAccess());
            var groups = new QuestionGroupService(factory, new TestManagementAccess());
            var domain = (await questions.DomainsAsync()).First().Id;
            var group = await groups.SaveAsync(
                new QuestionGroup { Title = "共享文章", Content = "  首行缩进\n\t第二行\n　　全角空格" }
            );
            var first = await questions.SaveAsync(
                new Question
                {
                    GroupId = group,
                    Text = "高分题",
                    Explanation = "第一行\n第二行",
                    Choices =
                    [
                        new()
                        {
                            Text = "正确\n换行",
                            ImageUrl = "/media/option-before.png",
                            IsCorrect = true,
                        },
                        new() { Text = "错误" },
                    ],
                },
                ["points-only"],
                [domain]
            );
            Assert(
                (await groups.ListAsync()).Single().Content == "  首行缩进\n\t第二行\n　　全角空格",
                "shared material preserves leading spaces, tabs and line breaks"
            );
            var second = await questions.SaveAsync(
                new Question
                {
                    Text = "低分题",
                    Choices =
                    [
                        new()
                        {
                            Text = "",
                            ImageUrl = "/media/a.png",
                            IsCorrect = true,
                        },
                        new() { Text = "", ImageUrl = "/media/b.png" },
                    ],
                },
                ["points-only"],
                [domain]
            );
            var quizId = await admin.SaveAsync(new Quiz { Title = "加权计分" });
            var other = await admin.SaveAsync(new Quiz { Title = "共享题另一卷" });
            await admin.AddQuestionsAsync(quizId, [first, second]);
            await admin.AddQuestionsAsync(other, [first]);
            await admin.SetPointsAsync(quizId, first, 3m);
            await admin.SetPointsAsync(quizId, second, 2m);
            await admin.SetPointsAsync(other, first, 10);
            Assert(
                (await quizzes.GetForAdminAsync(quizId))!.TotalPoints == 5m
                    && (await quizzes.GetForAdminAsync(other))!.TotalPoints == 10,
                "points belong to each preset, integer totals persist"
            );
            foreach (var invalid in new[] { 0m, -1m, 1001m, 1.5m, 0.1m })
                await Reject(() => admin.SetPointsAsync(quizId, first, invalid));
            await admin.PublishAsync(quizId);
            await Reject(() => admin.SaveAsync(new Quiz { Id = quizId, Title = "修改上架测试" }));
            await Reject(() => admin.AddQuestionsAsync(quizId, [first]));
            await Reject(() => admin.RemoveQuestionAsync(quizId, first));
            await Reject(() => admin.RemoveGroupAsync(quizId, group));
            await Reject(() => admin.PlaceAsync(quizId, first, null, true));
            await Reject(() => admin.SetPointsAsync(quizId, first, 5));
            var question = (await questions.ListAsync()).Single(q => q.Id == first);
            await Reject(() => questions.SaveAsync(question, [], [domain]));
            await Reject(() => questions.DeleteAsync(first));
            await Reject(() =>
                groups.SaveAsync(
                    new QuestionGroup
                    {
                        Id = group,
                        Title = "改材料",
                        Content = "新内容",
                    }
                )
            );
            Assert(
                (await quizzes.GetAsync(quizId))!.Title == "加权计分",
                "published metadata, composition, points and shared content are locked"
            );
            var attempt = await quizzes.SubmitAsync(
                quizId,
                new Dictionary<int, int> { { first, question.Choices.Single(c => c.IsCorrect).Id } },
                "points-user"
            );
            var result = (await quizzes.ResultAsync(attempt, "points-user"))!;
            Assert(
                result.Score == 3m
                    && result.Total == 5m
                    && result.Answers.Single(a => a.QuestionId == second).Points == 2m,
                "weighted score awards correct points and zero for unanswered"
            );
            Assert(
                result.Answers.First(a => a.QuestionId == first).Explanation == "第一行\n第二行",
                "explanation preserves line breaks in history"
            );
            Assert(
                result.Answers.Single(a => a.QuestionId == first).Options.Single(o => o.IsCorrect).ImageUrl
                    == "/media/option-before.png",
                "preset history snapshots option images"
            );
            var history = (await quizzes.HistoryAsync("points-user")).Single();
            Assert(
                history.CorrectCount == 1 && history.QuestionCount == 2,
                "profile accuracy counts answers, not points"
            );
            await admin.UnpublishAsync(quizId);
            await admin.SetPointsAsync(quizId, first, 20);
            Assert(
                (await quizzes.ResultAsync(attempt, "points-user"))!
                    .Answers.Single(a => a.QuestionId == first)
                    .Points == 3m,
                "history retains original point snapshot after edits"
            );
            question.Choices.Single(c => c.IsCorrect).ImageUrl = "/media/option-after.png";
            await questions.SaveAsync(question, ["points-only"], [domain]);
            Assert(
                (await quizzes.ResultAsync(attempt, "points-user"))!
                    .Answers.Single(a => a.QuestionId == first)
                    .Options.Single(o => o.IsCorrect)
                    .ImageUrl == "/media/option-before.png",
                "editing option image preserves previous history"
            );
            var random = new RandomQuizService(factory);
            var tag = (await random.TagsAsync()).Single(t => t.Name == "points-only");
            var paperId = await random.CreateAsync(new(2, null, [tag.Id], [domain]), "points-user");
            var paper = (await random.GetAsync(paperId, "points-user"))!.Value;
            var randomAttempt = await random.SubmitAsync(
                paperId,
                paper.Questions.ToDictionary(q => q.Id, q => q.Choices.Single(c => c.IsCorrect).Id),
                "points-user"
            );
            var randomResult = (await quizzes.ResultAsync(randomAttempt, "points-user"))!;
            Assert(
                randomResult.Score == 2
                    && randomResult.Total == 2
                    && randomResult.Answers.All(a => a.Points == 1),
                "random quizzes still score one point per question"
            );
            Assert(
                randomResult
                    .Answers.Single(a => a.QuestionId == first)
                    .Options.Single(o => o.IsCorrect)
                    .ImageUrl == "/media/option-after.png",
                "random history snapshots option images"
            );
            Assert(
                await random.AvailableAsync(new(2, null, [tag.Id], [domain])) == 2
                    && (await quizzes.ListAsync()).Count == 0,
                "random pool remains available with no published presets"
            );
            await using (var db = new QuizDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Choices DROP COLUMN ImageUrl");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE QuizQuestions DROP COLUMN Points");
                await db.Database.ExecuteSqlRawAsync("ALTER TABLE Answers DROP COLUMN Points");
                await DatabaseUpgrade.ApplyAsync(db);
                await DatabaseUpgrade.ApplyAsync(db);
                Assert(
                    (await db.Choices.ToListAsync()).All(c => c.ImageUrl == null),
                    "existing choices migrate with no attached image"
                );
                Assert(
                    (await db.QuizQuestions.ToListAsync()).All(i => i.Points == 1)
                        && (await db.Answers.ToListAsync()).All(a => a.Points == 1),
                    "old memberships and answer snapshots migrate to one point once"
                );
            }
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok)
            throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static async Task Reject(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Exception("Expected validation rejection");
    }
}
