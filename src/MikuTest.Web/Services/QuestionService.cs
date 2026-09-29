using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;
using MikuTest.Web.Models;

namespace MikuTest.Web.Services;

public class QuestionService(IDbContextFactory<QuizDbContext> factory, IManagementAccess access)
{
    public async Task<List<Tag>> TagsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync();
    }

    public async Task<List<Question>> ListAsync()
    {
        await access.RequireAsync(Permissions.QuestionView);
        await using var db = await factory.CreateDbContextAsync();
        return await db
            .Questions.AsNoTracking()
            .Include(q => q.Group)
            .Include(q => q.Choices)
            .Include(q => q.Media)
            .Include(q => q.Tags)
            .Include(q => q.Domains)
            .AsSplitQuery()
            .OrderBy(q => q.Id)
            .ToListAsync();
    }

    public async Task<List<KnowledgeDomain>> DomainsAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.KnowledgeDomains.AsNoTracking().OrderBy(d => d.Order).ToListAsync();
    }

    public async Task<int> SaveAsync(
        Question draft,
        IEnumerable<string>? tagNames = null,
        IEnumerable<int>? domainIds = null
    )
    {
        var actor = await access.RequireAsync(
            draft.Id == 0 ? Permissions.QuestionCreate : Permissions.QuestionEdit
        );
        Validate(draft);
        var selectedDomainIds = domainIds?.Distinct().ToList() ?? [];
        if (selectedDomainIds.Count == 0)
            throw new InvalidOperationException("请至少选择一个考察领域。");
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (draft.Id != 0)
            await QuizAdminService.EnsureQuestionEditableAsync(db, draft.Id);
        var question =
            draft.Id == 0
                ? new Question()
                : await db
                    .Questions.Include(q => q.Choices)
                    .Include(q => q.Media)
                    .Include(q => q.Tags)
                    .Include(q => q.Domains)
                    .SingleOrDefaultAsync(q => q.Id == draft.Id)
                    ?? throw new InvalidOperationException("题目已被删除，请刷新列表。");
        if (
            draft.Choices.Where(c => c.Id != 0).Any(c => question.Choices.All(old => old.Id != c.Id))
            || draft.Media.Where(m => m.Id != 0).Any(m => question.Media.All(old => old.Id != m.Id))
        )
            throw new InvalidOperationException("选项或媒体不属于此题，请重新打开编辑。");
        question.Text = draft.Text.Trim();
        question.Explanation = draft.Explanation.Trim();
        question.Difficulty = draft.Difficulty;
        if (draft.GroupId.HasValue && !await db.QuestionGroups.AnyAsync(g => g.Id == draft.GroupId))
            throw new InvalidOperationException("公共材料不存在，请重新选择题组。");
        if (question.GroupId != draft.GroupId)
        {
            question.GroupOrder = draft.GroupId.HasValue
                ? (
                    await db
                        .Questions.Where(q => q.GroupId == draft.GroupId)
                        .MaxAsync(q => (int?)q.GroupOrder)
                    ?? 0
                ) + 1
                : 0;
            question.GroupId = draft.GroupId;
        }
        foreach (var old in question.Choices.Where(c => draft.Choices.All(d => d.Id != c.Id)).ToList())
            db.Choices.Remove(old);
        foreach (var item in draft.Choices)
        {
            var choice = item.Id == 0 ? new Choice() : question.Choices.Single(c => c.Id == item.Id);
            choice.Text = item.Text.Trim();
            choice.ImageUrl = string.IsNullOrWhiteSpace(item.ImageUrl) ? null : item.ImageUrl.Trim();
            choice.IsCorrect = item.IsCorrect;
            if (item.Id == 0)
                question.Choices.Add(choice);
        }
        foreach (var old in question.Media.Where(m => draft.Media.All(d => d.Id != m.Id)).ToList())
            db.QuestionMedia.Remove(old);
        foreach (var item in draft.Media)
        {
            var media = item.Id == 0 ? new QuestionMedia() : question.Media.Single(m => m.Id == item.Id);
            media.Kind = item.Kind;
            media.Url = item.Url.Trim();
            media.Description = item.Description.Trim();
            if (item.Id == 0)
                question.Media.Add(media);
        }
        if (tagNames is not null)
        {
            var names = tagNames
                .Select(NormalizeTag)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (names.Count > 10)
                throw new InvalidOperationException("每题最多 10 个标签。");
            var existing = await db.Tags.ToListAsync();
            question.Tags.Clear();
            foreach (var name in names)
                question.Tags.Add(
                    existing.FirstOrDefault(t =>
                        string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                    ) ?? new Tag { Name = name }
                );
        }
        var domains = await db
            .KnowledgeDomains.Where(d => selectedDomainIds.Contains(d.Id))
            .OrderBy(d => d.Order)
            .ToListAsync();
        if (domains.Count != selectedDomainIds.Count)
            throw new InvalidOperationException("包含无效的考察领域，请刷新页面后重试。");
        question.Domains.Clear();
        foreach (var domain in domains)
            question.Domains.Add(domain);
        if (draft.Id == 0)
            db.Questions.Add(question);
        await ContentAudit.SaveAsync(
            db,
            actor,
            draft.Id == 0 ? "Question.Create" : "Question.Edit",
            "Question",
            () => question.Id
        );
        await transaction.CommitAsync();
        return question.Id;
    }

    public async Task DeleteAsync(int id)
    {
        var actor = await access.RequireAsync(Permissions.QuestionDelete);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await QuizAdminService.EnsureQuestionEditableAsync(db, id);
        var question =
            await db
                .Questions.Include(q => q.Choices)
                .Include(q => q.Media)
                .Include(q => q.Tags)
                .Include(q => q.Domains)
                .SingleOrDefaultAsync(q => q.Id == id)
            ?? throw new InvalidOperationException("题目已被删除，请刷新列表。");
        db.Questions.Remove(question);
        await ContentAudit.SaveAsync(db, actor, "Question.Delete", "Question", () => id);
        await transaction.CommitAsync();
    }

    private static void Validate(Question q)
    {
        if (string.IsNullOrWhiteSpace(q.Text) || q.Text.Length > 2000)
            throw new InvalidOperationException("题干必填，最多 2000 字。");
        if (q.Explanation.Length > 4000)
            throw new InvalidOperationException("解析最多 4000 字。");
        if (!Enum.IsDefined(q.Difficulty))
            throw new InvalidOperationException("请选择有效难度。");
        if (
            q.Choices.Count < 2
            || q.Choices.Count > 8
            || q.Choices.Any(c =>
                (string.IsNullOrWhiteSpace(c.Text) && string.IsNullOrWhiteSpace(c.ImageUrl))
                || c.Text.Length > 1000
            )
        )
            throw new InvalidOperationException("请填写 2～8 个选项，每项需有文字或图片，文字最多 1000 字。");
        if (q.Choices.Count(c => c.IsCorrect) != 1)
            throw new InvalidOperationException("单选题必须恰好设置一个正确答案。");
        if (
            q.Choices.Select(c => (c.Text.Trim().ToUpperInvariant(), c.ImageUrl)).Distinct().Count()
            != q.Choices.Count
        )
            throw new InvalidOperationException("选项内容不能完全重复。");
        if (
            q.Choices.Where(c => c.Id != 0).GroupBy(c => c.Id).Any(g => g.Count() > 1)
            || q.Media.Where(m => m.Id != 0).GroupBy(m => m.Id).Any(g => g.Count() > 1)
        )
            throw new InvalidOperationException("选项或媒体记录重复。");
        if (q.Choices.Any(c => c.ImageUrl is not null && !ValidUrl(c.ImageUrl)))
            throw new InvalidOperationException("选项图片地址无效。");
        if (
            q.Media.Count > 5
            || q.Media.Any(m => !Enum.IsDefined(m.Kind) || !ValidUrl(m.Url) || m.Description.Length > 500)
        )
            throw new InvalidOperationException(
                "最多 5 个媒体；地址须为 /media/… 形式的站内路径或 HTTPS 链接，描述最多 500 字。"
            );
    }

    internal static bool ValidUrl(string value)
    {
        var url = value.Trim();
        if (url.Length == 0 || url.Length > 2000 || url.Any(char.IsControl) || url.Contains('\\'))
            return false;
        return (url.StartsWith('/') && !url.StartsWith("//"))
            || (
                Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme == "https"
                && string.IsNullOrEmpty(uri.UserInfo)
            );
    }

    private static string NormalizeTag(string value)
    {
        var name = value.Trim();
        if (name.Length > 30)
            throw new InvalidOperationException("单个标签最多 30 字。");
        if (name.Any(char.IsControl) || name.Contains(',') || name.Contains('，'))
            throw new InvalidOperationException("请一次添加一个标签，不要包含逗号或换行。");
        return name;
    }
}
