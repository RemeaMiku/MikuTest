namespace MikuTest.Web.Models;

public sealed class BankFilter
{
    public string Search { get; set; } = "";
    public int? DomainId { get; set; }
    public int? TagId { get; set; }
    public Difficulty? Difficulty { get; set; }
    public string Type { get; set; } = "";
    public bool Descending { get; set; }

    public static bool Matches(int id, string title, string search)
    {
        var value = search.Trim();
        if (value.StartsWith('#'))
            return int.TryParse(value[1..], out var exactId) && id == exactId;
        return value.Length == 0
            || title.Contains(value, StringComparison.OrdinalIgnoreCase)
            || int.TryParse(value, out var numericId) && id == numericId;
    }

    public List<Question> Apply(IEnumerable<Question> questions)
    {
        var filtered = questions
            .Where(q => Matches(q.Id, q.Text, Search))
            .Where(q => !DomainId.HasValue || q.Domains.Any(d => d.Id == DomainId))
            .Where(q => !TagId.HasValue || q.Tags.Any(t => t.Id == TagId))
            .Where(q => !Difficulty.HasValue || q.Difficulty == Difficulty)
            .Where(q =>
                Type switch
                {
                    "text" => q.Media.Count == 0
                        && (q.Group?.Media.Count ?? 0) == 0
                        && q.Choices.All(c => string.IsNullOrEmpty(c.ImageUrl)),
                    "image" => q.Media.Any(m => m.Kind == MediaKind.Image)
                        || q.Group?.Media.Any(m => m.Kind == MediaKind.Image) == true
                        || q.Choices.Any(c => !string.IsNullOrEmpty(c.ImageUrl)),
                    "audio" => q.Media.Any(m => m.Kind == MediaKind.Audio)
                        || q.Group?.Media.Any(m => m.Kind == MediaKind.Audio) == true,
                    "video" => q.Media.Any(m => m.Kind == MediaKind.Video)
                        || q.Group?.Media.Any(m => m.Kind == MediaKind.Video) == true,
                    _ => true,
                }
            );
        return (Descending ? filtered.OrderByDescending(q => q.Id) : filtered.OrderBy(q => q.Id)).ToList();
    }

    public void Reset()
    {
        Search = Type = "";
        DomainId = TagId = null;
        Difficulty = null;
        Descending = false;
    }
}
