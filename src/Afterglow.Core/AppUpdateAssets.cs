namespace Afterglow.Core;

/// <summary>Picks Afterglow Windows installer assets and the newest stable GitHub tag.</summary>
public static class AppUpdateAssets
{
    public const string Owner = "goonedoutgames";
    public const string Repo = "afterglow";
    public const string WindowsInstallerFileName = "Afterglow-Setup-x64.exe";

    public static string ReleasesUrl => $"https://github.com/{Owner}/{Repo}/releases";

    public static bool IsWindowsInstaller(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var name = Path.GetFileName(fileName.Trim());
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Contains("avn-hub", StringComparison.OrdinalIgnoreCase)) return false;
        return name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
               || name.Contains("Afterglow-Setup", StringComparison.OrdinalIgnoreCase);
    }

    public static string? PickWindowsInstallerUrl(IEnumerable<(string Name, string Url)> assets)
    {
        var matches = assets
            .Where(a => IsWindowsInstaller(a.Name) && !string.IsNullOrWhiteSpace(a.Url))
            .ToList();
        if (matches.Count == 0) return null;
        var exact = matches.FirstOrDefault(a =>
            string.Equals(Path.GetFileName(a.Name), WindowsInstallerFileName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(exact.Url)) return exact.Url;
        return matches[0].Url;
    }

    public static string DirectInstallerUrl(string tagName)
    {
        var tag = tagName.Trim();
        if (tag.Length == 0) tag = "latest";
        else if (tag[0] is not 'v' and not 'V' && char.IsDigit(tag[0]))
            tag = "v" + tag;
        return $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{WindowsInstallerFileName}";
    }

    /// <summary>Highest stable tag that is newer than <paramref name="currentVersion"/>; otherwise null.</summary>
    public static string? PickNewestNewerStable(IEnumerable<string> tags, string currentVersion)
    {
        string? bestTag = null;
        string? bestVersion = null;
        foreach (var tag in tags)
        {
            var version = SemVer.Normalize(tag);
            if (!SemVer.IsNewerStable(currentVersion, version)) continue;
            if (bestVersion is null || SemVer.IsNewerStable(bestVersion, version))
            {
                bestVersion = version;
                bestTag = tag.Trim();
            }
        }
        return bestTag;
    }
}
