using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Models;

namespace MikuTest.Web.Data;

public static class Phase2Seed
{
    public static async Task InitializeAsync(QuizDbContext db)
    {
        if (await db.Tags.AnyAsync())
            return;
        var questions = await db
            .Questions.Include(q => q.Media)
            .Include(q => q.Tags)
            .OrderBy(q => q.Id)
            .ToListAsync();
        if (questions.Count == 0)
            return;
        var tags = new Dictionary<string, Tag>();
        foreach (var pair in questions.Select((q, i) => (q, order: i + 1)))
        {
            var q = pair.q;
            q.Difficulty = pair.order switch
            {
                1 => Difficulty.Easy,
                >= 4 => Difficulty.Hard,
                _ => Difficulty.Normal,
            };
            var name = q.Media.FirstOrDefault()?.Kind switch
            {
                MediaKind.Image => "图片识别",
                MediaKind.Audio => "听力",
                MediaKind.Video => "视频识别",
                _ => "基础知识",
            };
            if (!tags.TryGetValue(name, out var tag))
                tags[name] = tag = new Tag { Name = name };
            q.Tags.Add(tag);
        }
        await db.SaveChangesAsync();
    }
}
