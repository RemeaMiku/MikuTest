using Microsoft.EntityFrameworkCore;
using MikuTest.Web.Models;

namespace MikuTest.Web.Data;

public static class SeedData
{
    public static Task InitializeAsync(QuizDbContext db) =>
        SeedHistory.OnceAsync(db, "initial-quiz", () => SeedAsync(db));

    private static async Task SeedAsync(QuizDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        if (await db.Quizzes.AnyAsync())
            return;
        Question Make(
            int order,
            string text,
            string correct,
            string wrong,
            string explanation,
            QuestionMedia? media = null
        ) =>
            new()
            {
                Order = order,
                Text = text,
                Explanation = explanation,
                Choices = [new() { Text = correct, IsCorrect = true }, new() { Text = wrong }],
                Media = media is null ? [] : [media],
            };
        var questions = new List<Question>
        {
            Make(1, "初音未来的日文名字是？", "初音ミク", "鏡音リン", "初音未来对应日文名「初音ミク」。"),
            Make(
                2,
                "演示卡片上的数字是？",
                "39",
                "01",
                "这是本项目自制的 39 演示卡片。",
                new()
                {
                    Kind = MediaKind.Image,
                    Url = "/media/card.svg",
                    Description = "数字识别演示卡片",
                }
            ),
            Make(
                3,
                "播放音频：你听到了几次提示音？",
                "3 次",
                "1 次",
                "本地合成音频包含三次提示音，并非初音歌曲。",
                new()
                {
                    Kind = MediaKind.Audio,
                    Url = "/media/tones.wav",
                    Description = "自制提示音；听力组件演示",
                }
            ),
            Make(
                4,
                "播放视频：动画中的数字是？",
                "39",
                "16",
                "本地自制动画显示数字 39，并非官方 MV。",
                new()
                {
                    Kind = MediaKind.Video,
                    Url = "/media/demo.mp4",
                    Description = "自制数字动画；视频组件演示",
                }
            ),
        };
        db.Quizzes.Add(
            new Quiz
            {
                IsPublished = true,
                Title = "初音入门 · 多媒体体验",
                Description = "4 道单选题，体验知识、图片、音频和视频题。媒体均为技术演示素材。",
                Items = questions.Select((q, i) => new QuizQuestion { Question = q, Order = i + 1 }).ToList(),
            }
        );
        await db.SaveChangesAsync();
    }
}
