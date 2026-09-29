using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace MikuTest.Web.Models;

public enum MediaKind
{
    Image,
    Audio,
    Video,
}

public enum Difficulty
{
    Easy = 1,
    Normal = 2,
    Hard = 3,
}

public class Quiz
{
    public bool IsPublished { get; set; }
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsDeleted { get; set; }
    public List<QuizQuestion> Items { get; set; } = [];

    [NotMapped]
    public List<Question> Questions { get; set; } = [];

    [NotMapped]
    public decimal TotalPoints => Items.Sum(i => i.Points);
}

public class Question
{
    public int? GroupId { get; set; }
    public QuestionGroup? Group { get; set; }
    public int GroupOrder { get; set; }
    public int Id { get; set; }

    [NotMapped]
    public int Order { get; set; }

    [NotMapped]
    public decimal Points { get; set; } = 1;
    public string Text { get; set; } = "";
    public string Explanation { get; set; } = "";
    public Difficulty Difficulty { get; set; } = Difficulty.Normal;
    public List<Choice> Choices { get; set; } = [];
    public List<QuestionMedia> Media { get; set; } = [];
    public List<KnowledgeDomain> Domains { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
}

public class QuestionGroup
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string MediaJson { get; set; } = "[]";

    [NotMapped]
    public List<AnswerMediaSnapshot> Media
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<List<AnswerMediaSnapshot>>(MediaJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
        set => MediaJson = JsonSerializer.Serialize(value);
    }
}

public class QuizQuestion
{
    public decimal Points { get; set; } = 1;
    public int QuizId { get; set; }
    public int QuestionId { get; set; }
    public int Order { get; set; }
    public Quiz Quiz { get; set; } = default!;
    public Question Question { get; set; } = default!;
}

public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Question> Questions { get; set; } = [];
}

public class KnowledgeDomain
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Order { get; set; }
    public List<Question> Questions { get; set; } = [];
}

public class Choice
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public string Text { get; set; } = "";
    public string? ImageUrl { get; set; }
    public bool IsCorrect { get; set; }
}

public class QuestionMedia
{
    public int Id { get; set; }
    public int QuestionId { get; set; }
    public MediaKind Kind { get; set; }
    public string Url { get; set; } = "";
    public string Description { get; set; } = "";
}

public class Attempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int? QuizId { get; set; }
    public string QuizTitle { get; set; } = "";
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
    public string? UserId { get; set; }
    public bool IsRandom { get; set; }
    public decimal Score { get; set; }
    public decimal Total { get; set; }
    public List<Answer> Answers { get; set; } = [];

    [NotMapped]
    public bool CanRetry { get; set; }

    [NotMapped]
    public int CorrectCount { get; set; }

    [NotMapped]
    public int QuestionCount { get; set; }
}

// Snapshot text preserves historical results after question edits.
public class Answer
{
    public decimal Points { get; set; } = 1;
    public int? GroupId { get; set; }
    public string GroupTitle { get; set; } = "";
    public string GroupContent { get; set; } = "";
    public string GroupMediaJson { get; set; } = "[]";

    [NotMapped]
    public QuestionGroup? GroupSnapshot =>
        GroupId.HasValue
            ? new QuestionGroup
            {
                Id = GroupId.Value,
                Title = GroupTitle,
                Content = GroupContent,
                MediaJson = GroupMediaJson,
            }
            : null;
    public int Id { get; set; }
    public Guid AttemptId { get; set; }
    public int QuestionId { get; set; }
    public int? ChoiceId { get; set; }
    public int Order { get; set; }
    public bool IsCorrect { get; set; }
    public string QuestionText { get; set; } = "";
    public string SelectedText { get; set; } = "未作答";
    public string CorrectText { get; set; } = "";
    public string Explanation { get; set; } = "";
    public string OptionsJson { get; set; } = "[]";
    public string MediaJson { get; set; } = "[]";

    [NotMapped]
    public bool QuestionDeleted { get; set; }

    [NotMapped]
    public IReadOnlyList<AnswerOptionSnapshot> Options => ReadSnapshots<AnswerOptionSnapshot>(OptionsJson);

    [NotMapped]
    public IReadOnlyList<AnswerMediaSnapshot> Media => ReadSnapshots<AnswerMediaSnapshot>(MediaJson);

    private static IReadOnlyList<T> ReadSnapshots<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

public sealed record AnswerOptionSnapshot(
    string Text,
    bool IsSelected,
    bool IsCorrect,
    string? ImageUrl = null
);

public sealed record AnswerMediaSnapshot(MediaKind Kind, string Url, string Description);

public class RandomPaper
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? UserId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Title { get; set; } = "随机练习";
    public List<RandomPaperQuestion> Questions { get; set; } = [];
}

public class RandomPaperQuestion
{
    public Guid RandomPaperId { get; set; }
    public int QuestionId { get; set; }
    public int Order { get; set; }
    public Question Question { get; set; } = default!;
}
