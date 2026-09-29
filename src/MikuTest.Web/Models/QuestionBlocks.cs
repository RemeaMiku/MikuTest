namespace MikuTest.Web.Models;

public static class QuestionBlocks
{
    // A group occupies the position of its first selected question. Standalone questions remain separate.
    public static List<List<Question>> Split(IEnumerable<Question> questions) =>
        questions
            .GroupBy(q => q.GroupId.HasValue ? $"group:{q.GroupId}" : $"question:{q.Id}")
            .Select(g => g.ToList())
            .ToList();

    public static List<Question> Arrange(IEnumerable<Question> questions)
    {
        var result = Split(questions).SelectMany(g => g).ToList();
        for (var i = 0; i < result.Count; i++)
            result[i].Order = i + 1;
        return result;
    }
}
