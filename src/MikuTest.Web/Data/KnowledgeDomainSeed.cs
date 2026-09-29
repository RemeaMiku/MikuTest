using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Models;

namespace MikuTest.Web.Data;

public static class KnowledgeDomainSeed
{
    public static readonly string[] Names =
    [
        "角色与形象",
        "历史与文化",
        "歌曲与创作",
        "大型企划系列",
        "虚拟歌手大家族",
        "衍生角色和形象",
        "VOCALOID入门",
    ];

    private static readonly HashSet<string> ObsoleteTags =
    [
        "基础知识",
        "角色",
        "软件版本",
        "历史",
        "声库",
        "文化",
        "活动",
    ];

    private static readonly Dictionary<string, string[]> Assignments = new(StringComparer.Ordinal)
    {
        ["初音未来的日文名字是？"] = ["角色与形象"],
        ["演示卡片上的数字是？"] = ["角色与形象"],
        ["播放音频：你听到了几次提示音？"] = ["歌曲与创作", "VOCALOID入门"],
        ["播放视频：动画中的数字是？"] = ["歌曲与创作"],
        ["初音未来最初的软件形象编号是？"] = ["角色与形象"],
        ["初音未来最初于哪一年发布？"] = ["历史与文化"],
        ["初音未来由哪家公司企划开发？"] = ["历史与文化"],
        ["初音未来最初使用的是哪一代歌声合成引擎？"] = ["历史与文化", "VOCALOID入门"],
        ["为初音未来提供原声的是哪位声优？"] = ["角色与形象"],
        ["官方角色设定中的年龄是？"] = ["角色与形象"],
        ["官方角色设定中的身高是？"] = ["角色与形象"],
        ["官方角色设定中的体重是？"] = ["角色与形象"],
        ["“葱”与初音未来的关联主要来自哪里？"] = ["历史与文化", "衍生角色和形象"],
        ["“初音”这一名字表达的核心意象更接近哪项？"] = ["角色与形象"],
        ["MIKU EXPO 通常指什么？"] = ["大型企划系列"],
        ["图中最醒目的数字是？"] = ["角色与形象"],
        ["图中一共有几个星形？"] = ["角色与形象"],
        ["图中被突出显示的圆形是什么颜色？"] = ["角色与形象"],
        ["图中时间轴最左侧的年份是？"] = ["历史与文化"],
        ["播放音频：一共有几次提示音？"] = ["歌曲与创作", "VOCALOID入门"],
        ["播放音频：三次提示音的音高关系是？"] = ["歌曲与创作", "VOCALOID入门"],
        ["播放音频：整段音频时长最接近？"] = ["歌曲与创作"],
        ["播放音频：提示音之间是否留有明显静音？"] = ["歌曲与创作", "VOCALOID入门"],
        ["播放视频：画面中显示的主要数字是？"] = ["歌曲与创作"],
        ["播放视频：视频时长最接近？"] = ["歌曲与创作"],
        ["播放视频：主体在画面中呈现为什么形式？"] = ["歌曲与创作"],
        ["播放视频：画面的整体主色调更接近？"] = ["歌曲与创作"],
    };

    public static async Task InitializeAsync(QuizDbContext db)
    {
        var domains = await db.KnowledgeDomains.ToListAsync();
        for (var i = 0; i < Names.Length; i++)
        {
            var domain = domains.FirstOrDefault(x => x.Name == Names[i]);
            if (domain is null)
            {
                domain = new KnowledgeDomain { Name = Names[i], Order = i + 1 };
                domains.Add(domain);
                db.KnowledgeDomains.Add(domain);
            }
            else
                domain.Order = i + 1;
        }
        await db.SaveChangesAsync();

        var byName = domains.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var questions = await db.Questions.Include(q => q.Domains).ToListAsync();
        foreach (var question in questions.Where(q => q.Domains.Count == 0))
            if (Assignments.TryGetValue(question.Text, out var names))
                foreach (var name in names)
                    question.Domains.Add(byName[name]);

        var obsolete = await db.Tags.Where(t => ObsoleteTags.Contains(t.Name)).ToListAsync();
        db.Tags.RemoveRange(obsolete);
        await db.SaveChangesAsync();
    }
}
