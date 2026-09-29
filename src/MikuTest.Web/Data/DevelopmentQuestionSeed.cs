using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Models;

namespace MikuTest.Web.Data;

public static class DevelopmentQuestionSeed
{
    public const string QuizTitle = "综合测试题库 · 开发样例";

    public static Task InitializeAsync(QuizDbContext db) =>
        SeedHistory.OnceAsync(db, "development-quiz", () => SeedAsync(db));

    private static async Task SeedAsync(QuizDbContext db)
    {
        if (await db.Quizzes.AnyAsync(q => q.Title == QuizTitle))
            return;

        var tags = await db.Tags.ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase);
        Tag GetTag(string name)
        {
            if (tags.TryGetValue(name, out var tag))
                return tag;
            tag = new Tag { Name = name };
            tags[name] = tag;
            return tag;
        }

        Question Make(
            int order,
            Difficulty difficulty,
            string text,
            string correct,
            string explanation,
            string[] wrong,
            string[] tagNames,
            QuestionMedia? media = null
        )
        {
            var question = new Question
            {
                Order = order,
                Difficulty = difficulty,
                Text = text,
                Explanation = explanation,
                Choices =
                [
                    new Choice { Text = correct, IsCorrect = true },
                    .. wrong.Select(x => new Choice { Text = x }),
                ],
                Media = media is null ? [] : [media],
            };
            foreach (var name in tagNames.Append("开发样例").Distinct(StringComparer.OrdinalIgnoreCase))
                question.Tags.Add(GetTag(name));
            return question;
        }

        QuestionMedia Image(string url, string description) =>
            new()
            {
                Kind = MediaKind.Image,
                Url = url,
                Description = description,
            };
        QuestionMedia Audio(string description) =>
            new()
            {
                Kind = MediaKind.Audio,
                Url = "/media/tones.wav",
                Description = description,
            };
        QuestionMedia Video(string description) =>
            new()
            {
                Kind = MediaKind.Video,
                Url = "/media/demo.mp4",
                Description = description,
            };

        var questions = new List<Question>
        {
            Make(
                1,
                Difficulty.Easy,
                "初音未来的日文名字是？",
                "初音ミク",
                "初音未来的日文写法是「初音ミク」。",
                ["鏡音リン", "巡音ルカ", "MEIKO"],
                ["基础知识"]
            ),
            Make(
                2,
                Difficulty.Easy,
                "初音未来最初的软件形象编号是？",
                "01",
                "初音未来是 Character Vocal Series 的 01 号角色。",
                ["02", "03", "39"],
                ["角色", "基础知识"]
            ),
            Make(
                3,
                Difficulty.Easy,
                "初音未来最初于哪一年发布？",
                "2007 年",
                "初音未来最初于 2007 年发布。",
                ["2004 年", "2010 年", "2013 年"],
                ["历史"]
            ),
            Make(
                4,
                Difficulty.Easy,
                "初音未来由哪家公司企划开发？",
                "Crypton Future Media",
                "初音未来由 Crypton Future Media 企划开发。",
                ["SEGA", "Yamaha", "Sony Music"],
                ["基础知识", "软件版本"]
            ),
            Make(
                5,
                Difficulty.Normal,
                "初音未来最初使用的是哪一代歌声合成引擎？",
                "VOCALOID2",
                "2007 年的初代初音未来使用 VOCALOID2 引擎。",
                ["VOCALOID1", "VOCALOID3", "VOCALOID6"],
                ["软件版本", "历史"]
            ),
            Make(
                6,
                Difficulty.Normal,
                "为初音未来提供原声的是哪位声优？",
                "藤田咲",
                "初音未来的原声由声优藤田咲提供。",
                ["下田麻美", "浅川悠", "拝郷メイコ"],
                ["声库", "角色"]
            ),
            Make(
                7,
                Difficulty.Normal,
                "官方角色设定中的年龄是？",
                "16 岁",
                "初音未来的官方角色设定年龄为 16 岁。",
                ["14 岁", "18 岁", "20 岁"],
                ["角色"]
            ),
            Make(
                8,
                Difficulty.Normal,
                "官方角色设定中的身高是？",
                "158 cm",
                "初音未来的官方角色设定身高为 158 cm。",
                ["152 cm", "162 cm", "168 cm"],
                ["角色"]
            ),
            Make(
                9,
                Difficulty.Hard,
                "官方角色设定中的体重是？",
                "42 kg",
                "初音未来的官方角色设定体重为 42 kg。",
                ["39 kg", "45 kg", "48 kg"],
                ["角色"]
            ),
            Make(
                10,
                Difficulty.Hard,
                "“葱”与初音未来的关联主要来自哪里？",
                "早期网络二次创作文化",
                "葱是广为流传的粉丝文化符号，并非官方角色设定中的必备物品。",
                ["软件安装说明", "官方身高设定", "音源采样设备"],
                ["文化", "历史"]
            ),
            Make(
                11,
                Difficulty.Hard,
                "“初音”这一名字表达的核心意象更接近哪项？",
                "来自未来的第一声",
                "名称组合了“最初的声音”与“未来”的意象。",
                ["第一场演唱会", "第一台合成器", "第一张实体专辑"],
                ["角色", "文化"]
            ),
            Make(
                12,
                Difficulty.Hard,
                "MIKU EXPO 通常指什么？",
                "以初音未来为主题的海外巡演项目",
                "MIKU EXPO 是面向世界多地展开的初音未来主题演出项目。",
                ["声库编辑器", "插画比赛软件", "单曲排行榜"],
                ["活动", "文化"]
            ),
            Make(
                13,
                Difficulty.Easy,
                "图中最醒目的数字是？",
                "01",
                "测试图中央显示数字 01。",
                ["02", "16", "39"],
                ["图片识别", "角色"],
                Image("/media/test-01.svg", "自制 01 识别测试卡")
            ),
            Make(
                14,
                Difficulty.Easy,
                "图中一共有几个星形？",
                "3 个",
                "测试图中共有三个星形。",
                ["1 个", "2 个", "4 个"],
                ["图片识别"],
                Image("/media/test-stars.svg", "自制图形计数测试卡")
            ),
            Make(
                15,
                Difficulty.Normal,
                "图中被突出显示的圆形是什么颜色？",
                "蓝绿色",
                "中央圆形使用了初音主题常见的蓝绿色。",
                ["红色", "黄色", "紫色"],
                ["图片识别", "配色"],
                Image("/media/test-colors.svg", "自制颜色识别测试卡")
            ),
            Make(
                16,
                Difficulty.Hard,
                "图中时间轴最左侧的年份是？",
                "2007",
                "测试时间轴最左侧标注为 2007。",
                ["2010", "2013", "2016"],
                ["图片识别", "历史"],
                Image("/media/test-timeline.svg", "自制时间轴识别测试卡")
            ),
            Make(
                17,
                Difficulty.Easy,
                "播放音频：一共有几次提示音？",
                "3 次",
                "演示音频包含三次提示音。",
                ["1 次", "2 次", "4 次"],
                ["听力"],
                Audio("三次相同提示音；听力计数测试")
            ),
            Make(
                18,
                Difficulty.Normal,
                "播放音频：三次提示音的音高关系是？",
                "音高相同",
                "三次提示音使用相同频率。",
                ["逐次升高", "逐次降低", "高低交替"],
                ["听力"],
                Audio("三次相同提示音；音高辨别测试")
            ),
            Make(
                19,
                Difficulty.Normal,
                "播放音频：整段音频时长最接近？",
                "3 秒",
                "演示音频总时长约为 3 秒。",
                ["1 秒", "6 秒", "10 秒"],
                ["听力", "媒体识别"],
                Audio("三秒提示音；时长判断测试")
            ),
            Make(
                20,
                Difficulty.Hard,
                "播放音频：提示音之间是否留有明显静音？",
                "是",
                "三段提示音之间留有可辨别的静音间隔。",
                ["否，完全连续", "只有第一段后有", "只有最后一段前有"],
                ["听力", "媒体识别"],
                Audio("三次间隔提示音；结构判断测试")
            ),
            Make(
                21,
                Difficulty.Easy,
                "播放视频：画面中显示的主要数字是？",
                "39",
                "自制演示动画显示数字 39。",
                ["01", "16", "42"],
                ["视频识别"],
                Video("自制 39 动画；数字识别测试")
            ),
            Make(
                22,
                Difficulty.Normal,
                "播放视频：视频时长最接近？",
                "3 秒",
                "演示视频总时长约为 3 秒。",
                ["1 秒", "8 秒", "15 秒"],
                ["视频识别", "媒体识别"],
                Video("自制 39 动画；时长判断测试")
            ),
            Make(
                23,
                Difficulty.Hard,
                "播放视频：主体在画面中呈现为什么形式？",
                "带动画效果的数字",
                "画面主体是带动画效果的数字 39。",
                ["真人演唱", "歌词滚动", "乐谱演示"],
                ["视频识别"],
                Video("自制 39 动画；内容分类测试")
            ),
            Make(
                24,
                Difficulty.Hard,
                "播放视频：画面的整体主色调更接近？",
                "蓝绿色",
                "动画采用浅色背景和蓝绿色主题元素。",
                ["橙红色", "深紫色", "黑白灰"],
                ["视频识别", "配色"],
                Video("自制 39 动画；色调判断测试")
            ),
        };
        var quiz = new Quiz
        {
            Title = QuizTitle,
            IsPublished = true,
            Description =
                "24 道用于功能测试的单选题：文字、图片、音频、视频各有覆盖，简单、普通、困难各 8 题。",
            Items = questions.Select((q, i) => new QuizQuestion { Question = q, Order = i + 1 }).ToList(),
        };

        db.Quizzes.Add(quiz);
        await db.SaveChangesAsync();
    }
}
