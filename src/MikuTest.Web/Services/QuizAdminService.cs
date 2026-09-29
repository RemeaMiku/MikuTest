using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;
using MikuTest.Web.Models;

namespace MikuTest.Web.Services;

public class QuizAdminService(IDbContextFactory<QuizDbContext> factory, IManagementAccess access)
{
    public async Task<int> SaveAsync(Quiz draft)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        var title = draft.Title.Trim();
        var description = draft.Description.Trim();
        if (title.Length is < 1 or > 120)
            throw new InvalidOperationException("测试名称必填，最多 120 字。");
        if (description.Length > 1000)
            throw new InvalidOperationException("测试简介最多 1000 字。");
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (draft.Id != 0)
            await EnsureEditableAsync(db, draft.Id);
        if (await db.Quizzes.AnyAsync(q => q.Id != draft.Id && q.Title.ToLower() == title.ToLower()))
            throw new InvalidOperationException("已有同名测试。");
        var quiz =
            draft.Id == 0
                ? new Quiz()
                : await db.Quizzes.FindAsync(draft.Id)
                    ?? throw new InvalidOperationException("测试已被删除，请刷新列表。");
        quiz.Title = title;
        quiz.Description = description;
        if (draft.Id == 0)
            db.Quizzes.Add(quiz);
        await ContentAudit.SaveAsync(
            db,
            actor,
            draft.Id == 0 ? "Quiz.Create" : "Quiz.Edit",
            "Quiz",
            () => quiz.Id
        );
        await transaction.CommitAsync();
        return quiz.Id;
    }

    public async Task DeleteAsync(int id)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var quiz =
            await db.Quizzes.FindAsync(id)
            ?? throw new InvalidOperationException("测试已被删除，请刷新列表。");
        await db
            .Answers.Where(a => db.Attempts.Any(t => t.Id == a.AttemptId && t.QuizId == id))
            .ExecuteDeleteAsync();
        await db.Attempts.Where(a => a.QuizId == id).ExecuteDeleteAsync();
        await db.QuizQuestions.Where(q => q.QuizId == id).ExecuteDeleteAsync();
        db.Quizzes.Remove(quiz);
        await db.SaveChangesAsync();
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                "Quiz.Delete",
                "Quiz",
                id,
                before: new
                {
                    quiz.Title,
                    quiz.IsPublished,
                    quiz.IsDeleted,
                }
            )
        );
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task UnpublishAsync(int id)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        var quiz = await db.Quizzes.FindAsync(id) ?? throw new InvalidOperationException("测试不存在。");
        await using var transaction = await db.Database.BeginTransactionAsync();
        quiz.IsPublished = false;
        quiz.IsDeleted = true;
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                "Quiz.Unpublish",
                "Quiz",
                id,
                after: new
                {
                    quiz.Title,
                    quiz.IsPublished,
                    quiz.IsDeleted,
                }
            )
        );
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task PublishAsync(int id)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var quiz =
            await db
                .Quizzes.Include(q => q.Items)
                    .ThenInclude(i => i.Question)
                        .ThenInclude(q => q.Choices)
                .SingleOrDefaultAsync(q => q.Id == id)
            ?? throw new InvalidOperationException("测试不存在。");
        if (quiz.Items.Count == 0)
            throw new InvalidOperationException("请先编排至少一道题目，再上架测试。");
        if (
            quiz.Items.Any(i =>
                i.Question.Choices.Count < 2 || i.Question.Choices.Count(c => c.IsCorrect) != 1
            )
        )
            throw new InvalidOperationException("部分题目的选项或正确答案不完整，请先修正。");
        if (quiz.Items.Any(i => !ValidPoints(i.Points)))
            throw new InvalidOperationException("题目分值必须为 1～1000 的整数。");
        quiz.IsPublished = true;
        quiz.IsDeleted = false;
        await db.SaveChangesAsync();
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                "Quiz.Publish",
                "Quiz",
                id,
                after: new
                {
                    quiz.Title,
                    quiz.IsPublished,
                    quiz.IsDeleted,
                }
            )
        );
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task AddQuestionAsync(int quizId, int questionId)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        if (await db.QuizQuestions.AnyAsync(i => i.QuizId == quizId && i.QuestionId == questionId))
            throw new InvalidOperationException("这道题已经在该测试中。");
        await AddQuestionsAsync(quizId, [questionId]);
    }

    public async Task AddQuestionsAsync(int quizId, IEnumerable<int> questionIds)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        var ids = questionIds.Distinct().ToArray();
        if (ids.Length == 0)
            throw new InvalidOperationException("请至少选择一道题目。");
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await EnsureEditableAsync(db, quizId);
        var questions = await db.Questions.Where(q => ids.Contains(q.Id)).ToListAsync();
        if (questions.Count != ids.Length)
            throw new InvalidOperationException("部分题目已不存在，请刷新题库。");
        var ordered = await OrderedAsync(db, quizId);
        var used = ordered.Select(i => i.QuestionId).ToHashSet();
        foreach (
            var q in QuestionBlocks
                .Split(ids.Select(id => questions.Single(q => q.Id == id)))
                .SelectMany(g => g.OrderBy(q => q.GroupOrder).ThenBy(q => q.Id))
        )
        {
            if (used.Contains(q.Id))
                continue;
            var item = new QuizQuestion
            {
                QuizId = quizId,
                QuestionId = q.Id,
                Question = q,
                Order = 200000 + ordered.Count,
            };
            db.QuizQuestions.Add(item);
            ordered.Add(item);
        }
        ordered = GroupItems(ordered);
        await RewriteOrdersAsync(db, quizId, ordered);
        db.AuditLogs.Add(
            AuditLog.Create(actor, "Quiz.AddQuestions", "Quiz", quizId, after: new { QuestionIds = ids })
        );
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task RemoveGroupAsync(int quizId, int groupId)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await EnsureEditableAsync(db, quizId);
        var ordered = await OrderedAsync(db, quizId);
        db.QuizQuestions.RemoveRange(ordered.Where(i => i.Question.GroupId == groupId));
        await db.SaveChangesAsync();
        await RewriteOrdersAsync(db, quizId, ordered.Where(i => i.Question.GroupId != groupId).ToList());
        db.AuditLogs.Add(AuditLog.Create(actor, "Quiz.RemoveGroup", "Quiz", quizId, after: new { groupId }));
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task RemoveQuestionAsync(int quizId, int questionId)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await EnsureEditableAsync(db, quizId);
        var item =
            await db.QuizQuestions.SingleOrDefaultAsync(i => i.QuizId == quizId && i.QuestionId == questionId)
            ?? throw new InvalidOperationException("题目已不在此测试中。");
        db.QuizQuestions.Remove(item);
        await db.SaveChangesAsync();
        await RewriteOrdersAsync(db, quizId, await OrderedAsync(db, quizId));
        db.AuditLogs.Add(
            AuditLog.Create(actor, "Quiz.RemoveQuestion", "Quiz", quizId, after: new { questionId })
        );
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    public async Task PlaceAsync(int quizId, int questionId, int? anchorId, bool after)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await EnsureEditableAsync(db, quizId);
        var ordered = await OrderedAsync(db, quizId);
        var previousOrder = ordered.Select(i => i.QuestionId).ToArray();
        var source =
            ordered.SingleOrDefault(i => i.QuestionId == questionId)
            ?? throw new InvalidOperationException("题目已不在此测试中。");
        if (anchorId.HasValue && ordered.All(i => i.QuestionId != anchorId))
            throw new InvalidOperationException("目标题目已变更，请刷新页面。");
        if (anchorId == questionId)
            return;
        var anchor = anchorId.HasValue ? ordered.Single(i => i.QuestionId == anchorId) : null;
        var sameGroup =
            source.Question.GroupId.HasValue && anchor?.Question.GroupId == source.Question.GroupId;
        var moving =
            source.Question.GroupId.HasValue && !sameGroup
                ? ordered.Where(i => i.Question.GroupId == source.Question.GroupId).ToList()
                : [source];
        ordered.RemoveAll(i => moving.Contains(i));
        var target = after ? ordered.Count : 0;
        if (anchor is not null)
        {
            var anchors =
                anchor.Question.GroupId.HasValue && !sameGroup
                    ? ordered.Where(i => i.Question.GroupId == anchor.Question.GroupId).ToList()
                    : [anchor];
            target = after ? ordered.IndexOf(anchors.Last()) + 1 : ordered.IndexOf(anchors.First());
        }
        ordered.InsertRange(target, moving);
        await RewriteOrdersAsync(db, quizId, ordered);
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                "Quiz.Place",
                "Quiz",
                quizId,
                before: new { Order = previousOrder },
                after: new
                {
                    questionId,
                    anchorId,
                    after,
                    Order = ordered.Select(i => i.QuestionId).ToArray(),
                }
            )
        );
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private static bool ValidPoints(decimal points) =>
        points >= 1m && points <= 1000m && decimal.Truncate(points) == points;

    public async Task SetPointsAsync(int quizId, int questionId, decimal points)
    {
        var actor = await access.RequireAsync(Permissions.QuizManage);
        if (!ValidPoints(points))
            throw new InvalidOperationException("分值必须为 1～1000 的整数。");
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await EnsureEditableAsync(db, quizId);
        var item =
            await db.QuizQuestions.FindAsync(quizId, questionId)
            ?? throw new InvalidOperationException("题目已不在此测试中。");
        var previousPoints = item.Points;
        item.Points = points;
        await db.SaveChangesAsync();
        db.AuditLogs.Add(
            AuditLog.Create(
                actor,
                "Quiz.SetPoints",
                "Quiz",
                quizId,
                before: new { questionId, points = previousPoints },
                after: new { questionId, points }
            )
        );
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    internal static async Task EnsureEditableAsync(QuizDbContext db, int id)
    {
        var quiz =
            await db.Quizzes.FindAsync(id) ?? throw new InvalidOperationException("测试不存在，请刷新列表。");
        if (quiz.IsPublished)
            throw new InvalidOperationException("请先下架测试，再编辑或编排。");
    }

    internal static async Task EnsureQuestionEditableAsync(QuizDbContext db, int questionId)
    {
        if (await db.QuizQuestions.AnyAsync(i => i.QuestionId == questionId && i.Quiz.IsPublished))
            throw new InvalidOperationException("这道题被已上架测试引用，请先下架相关测试再修改或删除。");
    }

    private static List<QuizQuestion> GroupItems(IEnumerable<QuizQuestion> items) =>
        items
            .GroupBy(i => i.Question.GroupId.HasValue ? $"g:{i.Question.GroupId}" : $"q:{i.QuestionId}")
            .SelectMany(g => g)
            .ToList();

    private static async Task<List<QuizQuestion>> OrderedAsync(QuizDbContext db, int quizId) =>
        GroupItems(
            await db
                .QuizQuestions.Include(i => i.Question)
                .Where(i => i.QuizId == quizId)
                .OrderBy(i => i.Order)
                .ThenBy(i => i.QuestionId)
                .ToListAsync()
        );

    private static async Task RewriteOrdersAsync(QuizDbContext db, int quizId, List<QuizQuestion> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Order = 100000 + i;
        await db.SaveChangesAsync();
        for (var i = 0; i < ordered.Count; i++)
            ordered[i].Order = i + 1;
        await db.SaveChangesAsync();
    }
}
