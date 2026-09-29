using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Data;
using MikuTest.Web.Models;

namespace MikuTest.Web.Services;

public class QuestionGroupService(IDbContextFactory<QuizDbContext> factory, IManagementAccess access)
{
    public async Task<List<QuestionGroup>> ListAsync()
    {
        await access.RequireAsync(Permissions.QuestionView);
        await using var db = await factory.CreateDbContextAsync();
        return await db.QuestionGroups.AsNoTracking().OrderBy(g => g.Id).ToListAsync();
    }

    public async Task<int> SaveAsync(QuestionGroup draft)
    {
        var actor = await access.RequireAsync(
            draft.Id == 0 ? Permissions.QuestionCreate : Permissions.QuestionEdit
        );
        if (string.IsNullOrWhiteSpace(draft.Title) || draft.Title.Length > 120)
            throw new InvalidOperationException("材料标题必填，最多 120 字。");
        if (draft.Content.Length > 30000)
            throw new InvalidOperationException("公共材料最多 30000 字。");
        if (string.IsNullOrWhiteSpace(draft.Content) && draft.Media.Count == 0)
            throw new InvalidOperationException("请填写阅读材料或上传公共媒体。");
        if (
            draft.Media.Count > 5
            || draft.Media.Any(m =>
                !Enum.IsDefined(m.Kind) || !QuestionService.ValidUrl(m.Url) || m.Description.Length > 500
            )
        )
            throw new InvalidOperationException("公共媒体无效，每组最多 5 个。");
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (
            draft.Id != 0
            && await db.QuizQuestions.AnyAsync(i => i.Question.GroupId == draft.Id && i.Quiz.IsPublished)
        )
            throw new InvalidOperationException("公共材料被已上架测试引用，请先下架相关测试再编辑。");
        var group =
            draft.Id == 0
                ? new QuestionGroup()
                : await db.QuestionGroups.FindAsync(draft.Id)
                    ?? throw new InvalidOperationException("题组不存在。");
        group.Title = draft.Title.Trim();
        group.Content = draft.Content;
        group.Media = draft.Media;
        if (draft.Id == 0)
            db.QuestionGroups.Add(group);
        await ContentAudit.SaveAsync(
            db,
            actor,
            draft.Id == 0 ? "Material.Create" : "Material.Edit",
            "QuestionGroup",
            () => group.Id
        );
        await transaction.CommitAsync();
        return group.Id;
    }

    public async Task DeleteAsync(int id)
    {
        var actor = await access.RequireAsync(Permissions.QuestionDelete);
        await using var db = await factory.CreateDbContextAsync();
        if (await db.Questions.AnyAsync(q => q.GroupId == id))
            throw new InvalidOperationException("请先移出或删除组内小题，再删除空题组。");
        var group =
            await db.QuestionGroups.FindAsync(id) ?? throw new InvalidOperationException("题组不存在。");
        await using var tx = await db.Database.BeginTransactionAsync();
        db.QuestionGroups.Remove(group);
        await ContentAudit.SaveAsync(db, actor, "Material.Delete", "QuestionGroup", () => id);
        await tx.CommitAsync();
    }

    public async Task MoveAsync(int groupId, int questionId, int direction)
    {
        var actor = await access.RequireAsync(Permissions.QuestionEdit);
        await using var db = await factory.CreateDbContextAsync();
        var items = await db
            .Questions.Where(q => q.GroupId == groupId)
            .OrderBy(q => q.GroupOrder)
            .ThenBy(q => q.Id)
            .ToListAsync();
        var index = items.FindIndex(q => q.Id == questionId);
        var target = index + Math.Sign(direction);
        if (index < 0 || target < 0 || target >= items.Count)
            return;
        (items[index], items[target]) = (items[target], items[index]);
        for (var i = 0; i < items.Count; i++)
            items[i].GroupOrder = i + 1;
        await using var tx = await db.Database.BeginTransactionAsync();
        await ContentAudit.SaveAsync(db, actor, "Material.Reorder", "QuestionGroup", () => groupId);
        await tx.CommitAsync();
    }
}
