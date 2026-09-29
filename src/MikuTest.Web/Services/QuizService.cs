using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;
using MikuTest.Web.Models;

namespace MikuTest.Web.Services;

public class QuizService(IDbContextFactory<QuizDbContext> factory, IManagementAccess access)
{
    public async Task<List<Quiz>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        var quizzes = await db
            .Quizzes.AsNoTracking()
            .Where(q => q.IsPublished && !q.IsDeleted)
            .Include(q => q.Items)
                .ThenInclude(i => i.Question)
            .OrderBy(q => q.Id)
            .ToListAsync();
        foreach (var quiz in quizzes)
            Hydrate(quiz);
        return quizzes;
    }

    public async Task<List<Quiz>> AdminListAsync()
    {
        await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        var quizzes = await db
            .Quizzes.AsNoTracking()
            .Include(q => q.Items)
                .ThenInclude(i => i.Question)
            .OrderBy(q => q.Id)
            .ToListAsync();
        foreach (var quiz in quizzes)
            Hydrate(quiz);
        return quizzes;
    }

    public Task<Quiz?> GetAsync(int id) => GetCoreAsync(id, false);

    public Task<Quiz?> GetForAdminAsync(int id) => GetCoreAsync(id, true);

    private async Task<Quiz?> GetCoreAsync(int id, bool forAdmin)
    {
        if (forAdmin)
            await access.RequireAsync(Permissions.QuizManage);
        await using var db = await factory.CreateDbContextAsync();
        var quiz = await db
            .Quizzes.AsNoTracking()
            .Include(q => q.Items)
                .ThenInclude(i => i.Question)
                    .ThenInclude(q => q.Group)
            .Include(q => q.Items)
                .ThenInclude(i => i.Question)
                    .ThenInclude(q => q.Choices)
            .Include(q => q.Items)
                .ThenInclude(i => i.Question)
                    .ThenInclude(q => q.Media)
            .Include(q => q.Items)
                .ThenInclude(i => i.Question)
                    .ThenInclude(q => q.Tags)
            .Include(q => q.Items)
                .ThenInclude(i => i.Question)
                    .ThenInclude(q => q.Domains)
            .AsSplitQuery()
            .SingleOrDefaultAsync(q => q.Id == id && (forAdmin || q.IsPublished && !q.IsDeleted));
        if (quiz is not null)
            Hydrate(quiz);
        return quiz;
    }

    public async Task<Guid> SubmitAsync(
        int quizId,
        IReadOnlyDictionary<int, int> selections,
        string? userId = null
    )
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var quiz = await GetAsync(quizId) ?? throw new InvalidOperationException("测试不存在。");
        if (quiz.Questions.Count == 0)
            throw new InvalidOperationException("测试尚无题目。");
        if (selections.Keys.Any(id => quiz.Questions.All(q => q.Id != id)))
            throw new InvalidOperationException("包含不属于本测试的题目。");
        var attempt = new Attempt
        {
            QuizId = quiz.Id,
            QuizTitle = quiz.Title,
            Total = quiz.TotalPoints,
            UserId = userId,
        };
        foreach (var q in quiz.Questions.OrderBy(q => q.Order))
        {
            var correct = q.Choices.Single(c => c.IsCorrect);
            Choice? selected = null;
            if (selections.TryGetValue(q.Id, out var choiceId))
                selected =
                    q.Choices.SingleOrDefault(c => c.Id == choiceId)
                    ?? throw new InvalidOperationException("选项不属于此题。");
            // 保存作答时的选项、媒体与解析，后续编辑题库不会改写历史成绩。
            attempt.Answers.Add(
                new Answer
                {
                    Points = q.Points,
                    QuestionId = q.Id,
                    ChoiceId = selected?.Id,
                    Order = q.Order,
                    QuestionText = q.Text,
                    GroupId = q.GroupId,
                    GroupTitle = q.Group?.Title ?? "",
                    GroupContent = q.Group?.Content ?? "",
                    GroupMediaJson = q.Group?.MediaJson ?? "[]",
                    SelectedText = selected?.Text ?? "未作答",
                    CorrectText = correct.Text,
                    Explanation = q.Explanation,
                    IsCorrect = selected?.IsCorrect == true,
                    OptionsJson = JsonSerializer.Serialize(
                        q.Choices.Select(c => new AnswerOptionSnapshot(
                            c.Text,
                            c.Id == selected?.Id,
                            c.IsCorrect,
                            c.ImageUrl
                        ))
                    ),
                    MediaJson = JsonSerializer.Serialize(
                        q.Media.Select(m => new AnswerMediaSnapshot(m.Kind, m.Url, m.Description))
                    ),
                }
            );
        }
        attempt.Score = attempt.Answers.Where(a => a.IsCorrect).Sum(a => a.Points);
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return attempt.Id;
    }

    public async Task<Attempt?> ResultAsync(Guid id, string? userId = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        var attempt = await db
            .Attempts.AsNoTracking()
            .Include(a => a.Answers)
            .SingleOrDefaultAsync(a => a.Id == id && (a.UserId == null || a.UserId == userId));
        if (attempt is null)
            return null;
        attempt.CanRetry =
            attempt.IsRandom
            || attempt.QuizId.HasValue
                && await db.Quizzes.AnyAsync(q => q.Id == attempt.QuizId && q.IsPublished && !q.IsDeleted);
        var answerQuestionIds = attempt.Answers.Select(a => a.QuestionId).ToArray();
        var existing = (
            await db.Questions.Where(q => answerQuestionIds.Contains(q.Id)).Select(q => q.Id).ToListAsync()
        ).ToHashSet();
        foreach (var answer in attempt.Answers)
            answer.QuestionDeleted = !existing.Contains(answer.QuestionId);
        return attempt;
    }

    public async Task<List<Attempt>> HistoryAsync(string userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db
            .Attempts.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.SubmittedAtUtc)
            .Select(a => new Attempt
            {
                Id = a.Id,
                QuizId = a.QuizId,
                QuizTitle = a.QuizTitle,
                SubmittedAtUtc = a.SubmittedAtUtc,
                UserId = a.UserId,
                IsRandom = a.IsRandom,
                Score = a.Score,
                Total = a.Total,
                CorrectCount = a.Answers.Count(x => x.IsCorrect),
                QuestionCount = a.Answers.Count,
            })
            .ToListAsync();
    }

    private static void Hydrate(Quiz quiz)
    {
        foreach (var item in quiz.Items)
            item.Question.Points = item.Points;
        quiz.Questions = QuestionBlocks.Arrange(
            quiz.Items.OrderBy(i => i.Order).ThenBy(i => i.QuestionId).Select(i => i.Question)
        );
    }
}
