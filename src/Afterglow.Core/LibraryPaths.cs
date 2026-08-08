namespace Afterglow.Core;

/// <summary>Helpers for local library install folders (Steam-like named directories).</summary>
public static class LibraryPaths
{
    public static string InstallDirectory(string libraryRoot, long gameId, string? title)
    {
        var folder = SanitizeFolderName(title);
        if (string.IsNullOrWhiteSpace(folder))
            folder = $"game-{gameId}";
        // Disambiguate collisions while keeping a human-readable name.
        var candidate = Path.Combine(libraryRoot, folder);
        if (!Directory.Exists(candidate))
            return candidate;
        var tagged = Path.Combine(libraryRoot, $"{folder} [{gameId}]");
        return tagged;
    }

    public static string SanitizeFolderName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = title.Trim().Select(c => invalid.Contains(c) || c < 32 ? ' ' : c).ToArray();
        var cleaned = new string(chars);
        while (cleaned.Contains("  ", StringComparison.Ordinal))
            cleaned = cleaned.Replace("  ", " ", StringComparison.Ordinal);
        cleaned = cleaned.Trim(' ', '.', '_', '-');
        if (cleaned.Length > 80)
            cleaned = cleaned[..80].Trim();
        return cleaned;
    }

    public static string FormatPlaytime(long seconds)
    {
        if (seconds <= 0) return "0m";
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600)
        {
            var mins = seconds / 60.0;
            return mins < 10 ? $"{mins:0.#}m" : $"{mins:0}m";
        }
        return $"{seconds / 3600.0:0.0}h";
    }
}
