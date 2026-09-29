using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;
using MikuTest.Web.Models;
using MikuTest.Web.Services;

static class GroupChecks
{
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mikutest-groups-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<QuizDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            await using (var db = new QuizDbContext(options))
            {
                await DatabaseUpgrade.ApplyAsync(db);
                await DatabaseUpgrade.ApplyAsync(db);
                db.KnowledgeDomains.AddRange(
                    new KnowledgeDomain { Name = "领域 A", Order = 1 },
                    new KnowledgeDomain { Name = "领域 B", Order = 2 }
                );
                await db.SaveChangesAsync();
            }
            var factory = new Factory(options);
            var groups = new QuestionGroupService(factory, new TestManagementAccess());
            var questions = new QuestionService(factory, new TestManagementAccess());
            var quizzes = new QuizService(factory, new TestManagementAccess());
            var admin = new QuizAdminService(factory, new TestManagementAccess());
            var random = new RandomQuizService(factory);
            var domains = await questions.DomainsAsync();
            var groupId = await groups.SaveAsync(
                new QuestionGroup
                {
                    Title = "阅读材料",
                    Content = "第一段\n第二段",
                    Media = [new(MediaKind.Audio, "/media/tones.wav", "公共听力")],
                }
            );
            var ids = new List<int>();
            for (var i = 1; i <= 4; i++)
                ids.Add(
                    await questions.SaveAsync(
                        new Question
                        {
                            GroupId = groupId,
                            Text = $"小题 {i}",
                            Choices = [new() { Text = "正确", IsCorrect = true }, new() { Text = "错误" }],
                        },
                        ["reading-test"],
                        [domains[i % 2 == 0 ? 1 : 0].Id]
                    )
                );
            var standalone = await questions.SaveAsync(
                new Question
                {
                    Text = "独立题",
                    Choices = [new() { Text = "A", IsCorrect = true }, new() { Text = "B" }],
                },
                [],
                [domains[0].Id]
            );
            var bank = await questions.ListAsync();
            var filter = new BankFilter { DomainId = domains[1].Id, Type = "audio" };
            Assert(
                filter.Apply(bank).Select(q => q.Id).SequenceEqual(new[] { ids[1], ids[3] }),
                "filters target subquestions and include shared media type"
            );
            var quizId = await admin.SaveAsync(new Quiz { Title = "部分题组测试" });
            await admin.AddQuestionsAsync(quizId, [ids[1], standalone, ids[3]]);
            var quiz = (await quizzes.GetForAdminAsync(quizId))!;
            Assert(
                quiz.Questions.Select(q => q.Id).SequenceEqual(new[] { ids[1], ids[3], standalone }),
                "selected subset stays together without unselected siblings"
            );
            Assert(
                quiz.Questions[0].Group?.Content == "第一段\n第二段"
                    && quiz.Questions[0].Group?.Media.Count == 1,
                "selected subquestions load complete shared material"
            );
            await admin.AddQuestionsAsync(quizId, [ids[1], ids[3]]);
            Assert(
                (await quizzes.GetForAdminAsync(quizId))!.Questions.Count == 3,
                "bulk add skips duplicate memberships"
            );
            await admin.PlaceAsync(quizId, ids[1], null, true);
            Assert(
                (await quizzes.GetForAdminAsync(quizId))!
                    .Questions.Select(q => q.Id)
                    .SequenceEqual(new[] { standalone, ids[1], ids[3] }),
                "moving a group keeps the whole block together"
            );
            await admin.PlaceAsync(quizId, ids[3], ids[1], false);
            Assert(
                (await quizzes.GetForAdminAsync(quizId))!
                    .Questions.Select(q => q.Id)
                    .SequenceEqual(new[] { standalone, ids[3], ids[1] }),
                "subquestions can reorder within a group"
            );
            await admin.AddQuestionsAsync(quizId, [ids[0]]);
            Assert(
                (await quizzes.GetForAdminAsync(quizId))!.Questions.Count == 4,
                "remaining subquestion can be added later"
            );
            quiz = (await quizzes.GetForAdminAsync(quizId))!;
            await admin.PublishAsync(quizId);
            var attempt = await quizzes.SubmitAsync(
                quizId,
                quiz.Questions.ToDictionary(q => q.Id, q => q.Choices.Single(c => c.IsCorrect).Id),
                "group-user"
            );
            Assert(
                (await quizzes.ResultAsync(attempt, "group-user"))!.Score == 4,
                "group scoring counts each selected subquestion"
            );
            var tag = (await questions.TagsAsync()).Single(t => t.Name == "reading-test");
            var paperId = await random.CreateAsync(new(2, null, [tag.Id], [domains[1].Id]), "group-user");
            var paper = (await random.GetAsync(paperId, "group-user"))!.Value;
            Assert(
                paper.Questions.Select(q => q.Id).Order().SequenceEqual(new[] { ids[1], ids[3] }.Order())
                    && paper.Questions.All(q => q.Group is not null),
                "random selection takes exact matching subset with material"
            );
            var randomAttempt = await random.SubmitAsync(paperId, new Dictionary<int, int>(), "group-user");
            await admin.UnpublishAsync(quizId);
            await groups.SaveAsync(
                new QuestionGroup
                {
                    Id = groupId,
                    Title = "更新材料",
                    Content = "新的文章",
                }
            );
            Assert(
                (await quizzes.ResultAsync(attempt, "group-user"))!
                    .Answers.First(a => a.GroupId.HasValue)
                    .GroupSnapshot!.Content == "第一段\n第二段",
                "fixed history keeps original material snapshot after edits"
            );
            Assert(
                (await quizzes.ResultAsync(randomAttempt, "group-user"))!.Answers.All(a =>
                    a.GroupSnapshot?.Media.Count == 1
                ),
                "random history retains shared media snapshot"
            );
            await Reject(() => groups.DeleteAsync(groupId));
            await admin.RemoveQuestionAsync(quizId, ids[1]);
            Assert(
                (await quizzes.GetForAdminAsync(quizId))!.Questions.Any(q => q.Id == ids[3]),
                "removing one subquestion preserves selected siblings"
            );
            await admin.RemoveGroupAsync(quizId, groupId);
            Assert(
                (await quizzes.GetForAdminAsync(quizId))!.Questions.Count == 1
                    && (await questions.ListAsync()).Count == 5,
                "removing material block preserves question bank"
            );
            foreach (var id in ids)
                await questions.DeleteAsync(id);
            await groups.DeleteAsync(groupId);
            Assert(
                (await quizzes.ResultAsync(attempt, "group-user"))!
                    .Answers.Where(a => a.GroupId.HasValue)
                    .All(a => a.QuestionDeleted && a.GroupSnapshot?.Title == "阅读材料"),
                "history survives deleting questions and material"
            );
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
        throw new Exception("Non-empty group was deleted");
    }
}
