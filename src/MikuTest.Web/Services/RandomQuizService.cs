using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;
using MikuTest.Web.Models;

namespace MikuTest.Web.Services;

public record RandomQuizFilter(
    int Count,
    Difficulty? Difficulty,
    IReadOnlyList<int> TagIds,
    IReadOnlyList<int> DomainIds
);

public class RandomQuizService(IDbContextFactory<QuizDbContext> factory)
{
    public async Task<List<Tag>> TagsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<List<KnowledgeDomain>> DomainsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.KnowledgeDomains.AsNoTracking().OrderBy(d => d.Order).ToListAsync();
    }

    public async Task<int> AvailableAsync(RandomQuizFilter filter)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await Query(db, filter).CountAsync();
    }

    public async Task<Guid> CreateAsync(RandomQuizFilter filter, string? userId)
    {
        if (filter.Count is < 1 or > 50)
            throw new InvalidOperationException("题目数量必须在 1～50 之间。");
        await using var db = await factory.CreateDbContextAsync();
        var candidates = await Query(db, filter).ToListAsync();
        if (candidates.Count < filter.Count)
            throw new InvalidOperationException(
                $"符合条件的题目只有 {candidates.Count} 道，请减少题量或放宽筛选条件。"
            );
        // 先筛选和抽取小题，再把同一材料的小题排在一起，不强制抽取整个题组。
        var selected = QuestionBlocks
            .Split(candidates.OrderBy(_ => Random.Shared.Next()).Take(filter.Count))
            .SelectMany(g => g.OrderBy(q => q.GroupOrder).ThenBy(q => q.Id))
            .Select(q => q.Id)
            .ToList();
        var paper = new RandomPaper
        {
            UserId = userId,
            Title = BuildTitle(filter),
            Questions = selected
                .Select((id, i) => new RandomPaperQuestion { QuestionId = id, Order = i + 1 })
                .ToList(),
        };
        db.RandomPapers.Add(paper);
        await db.SaveChangesAsync();
        return paper.Id;
    }

    public async Task<(RandomPaper Paper, List<Question> Questions)?> GetAsync(Guid id, string? userId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var paper = await db
            .RandomPapers.AsNoTracking()
            .Include(p => p.Questions)
            .SingleOrDefaultAsync(p => p.Id == id);
        if (paper is null || paper.UserId is not null && paper.UserId != userId)
            return null;
        var ids = paper.Questions.Select(x => x.QuestionId).ToList();
        var questions = await db
            .Questions.AsNoTracking()
            .Where(q => ids.Contains(q.Id))
            .Include(q => q.Group)
            .Include(q => q.Choices)
            .Include(q => q.Media)
            .Include(q => q.Tags)
            .Include(q => q.Domains)
            .AsSplitQuery()
            .ToListAsync();
        var ordered = QuestionBlocks.Arrange(
            questions.OrderBy(q => paper.Questions.Single(x => x.QuestionId == q.Id).Order)
        );
        return (paper, ordered);
    }

    public async Task<Guid> SubmitAsync(
        Guid paperId,
        IReadOnlyDictionary<int, int> selections,
        string? userId
    )
    {
        var data =
            await GetAsync(paperId, userId)
            ?? throw new InvalidOperationException("随机试卷不存在或无权访问。");
        if (data.Questions.Count == 0)
            throw new InvalidOperationException("试卷中没有可用题目。");
        if (selections.Keys.Any(id => data.Questions.All(q => q.Id != id)))
            throw new InvalidOperationException("包含不属于本试卷的题目。");
        var attempt = new Attempt
        {
            QuizId = null,
            QuizTitle = data.Paper.Title,
            Total = data.Questions.Count,
            UserId = userId,
            IsRandom = true,
        };
        for (var i = 0; i < data.Questions.Count; i++)
        {
            var q = data.Questions[i];
            var correct = q.Choices.Single(c => c.IsCorrect);
            Choice? selected = null;
            if (selections.TryGetValue(q.Id, out var choiceId))
                selected =
                    q.Choices.SingleOrDefault(c => c.Id == choiceId)
                    ?? throw new InvalidOperationException("选项不属于此题。");
            attempt.Answers.Add(
                new Answer
                {
                    QuestionId = q.Id,
                    ChoiceId = selected?.Id,
                    Order = i + 1,
                    GroupId = q.GroupId,
                    GroupTitle = q.Group?.Title ?? "",
                    GroupContent = q.Group?.Content ?? "",
                    GroupMediaJson = q.Group?.MediaJson ?? "[]",
                    QuestionText = q.Text,
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
        attempt.Score = attempt.Answers.Count(a => a.IsCorrect);
        await using var db = await factory.CreateDbContextAsync();
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt.Id;
    }

    private static IQueryable<Question> Query(QuizDbContext db, RandomQuizFilter filter)
    {
        var query = db.Questions.AsNoTracking().AsQueryable();
        if (filter.Difficulty.HasValue)
            query = query.Where(q => q.Difficulty == filter.Difficulty);
        if (filter.TagIds.Count > 0)
            query = query.Where(q => q.Tags.Any(t => filter.TagIds.Contains(t.Id)));
        if (filter.DomainIds.Count > 0)
            query = query.Where(q => q.Domains.Any(d => filter.DomainIds.Contains(d.Id)));
        return query;
    }

    private static string BuildTitle(RandomQuizFilter filter)
    {
        var difficulty = filter.Difficulty switch
        {
            Difficulty.Easy => "简单",
            Difficulty.Normal => "普通",
            Difficulty.Hard => "困难",
            _ => "全难度",
        };
        return $"随机练习 · {difficulty} · {filter.Count} 题";
    }
}
