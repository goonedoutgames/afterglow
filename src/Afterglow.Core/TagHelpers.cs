namespace Afterglow.Core;

public static class TagHelpers
{
    /// <summary>Trim, drop empties and pure-numeric F95 SAM ids — matches web humanTags().</summary>
    public static List<string> HumanTags(IEnumerable<string>? tags)
    {
        if (tags is null) return [];
        var list = new List<string>();
        foreach (var raw in tags)
        {
            var t = raw?.Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (t.All(char.IsDigit)) continue;
            list.Add(t);
        }
        return list;
    }
}
