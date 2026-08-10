using Afterglow.Core.Models;

namespace Afterglow.Core;

/// <summary>
/// Normalizes F95 masked / hoster download links and infers platform labels client-side
/// (works even before the hub scrape improvements are deployed).
/// </summary>
public static class DownloadLinkNormalizer
{
    public sealed record NormalizedLink(
        string Url,
        string Host,
        string? Platform,
        string? Title,
        string DisplayName,
        bool IsMasked,
        string? OpenInBrowserUrl);

    public static IEnumerable<NormalizedLink> NormalizeAll(IEnumerable<DownloadLink> links) =>
        links.Select(Normalize).Where(x => x is not null).Cast<NormalizedLink>()
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

    public static NormalizedLink? Normalize(DownloadLink link)
    {
        if (link is null || string.IsNullOrWhiteSpace(link.Url))
            return null;

        var url = link.Url.Trim();
        var lower = url.ToLowerInvariant();
        if (IsJunk(lower))
            return null;

        var isMasked = lower.Contains("f95zone.to/masked/")
                       || lower.Contains("f95zone.to/masked-navigation");

        var host = ClassifyHost(url, link.Host);
        if (host == "skip")
            return null;

        // Prefer URL hints, then hub label (may already be Windows/Linux or PC).
        var platform = InferPlatform(url) ?? InferPlatform(link.Label) ?? NormalizePlatformLabel(link.Label);
        var title = CleanSectionTitle(link.Title);
        var display = BuildDisplayName(url, host, platform, title, link.Label);
        return new NormalizedLink(url, host, platform, title, display, isMasked, isMasked ? url : null);
    }

    public static string ClassifyHost(string url, string? reportedHost = null)
    {
        var u = url.ToLowerInvariant();
        var maskedTarget = ExtractMaskedTargetHost(u);
        if (maskedTarget is not null)
            u = maskedTarget;

        if (IsJunk(u)) return "skip";
        if (u.Contains("gofile.io")) return "gofile";
        if (u.Contains("mega.nz") || u.Contains("mega.co.nz")) return "mega";
        if (u.Contains("pixeldrain.com")) return "pixeldrain";
        if (u.Contains("datanodes.to")) return "datanodes";
        if (u.Contains("buzzheavier.com")) return "buzzheavier";
        if (u.Contains("vikingfile.com")) return "vikingfile";
        if (u.Contains("attachments.f95zone.to") || u.Contains("f95zone.to/attachments/")) return "f95";
        if (u.Contains("mixdrop.")) return "mixdrop";
        if (u.Contains("uploadhaven.com")) return "uploadhaven";
        if (u.Contains("mediafire.com")) return "mediafire";
        if (u.Contains("workupload.com")) return "workupload";
        if (u.Contains("drive.google.com")) return "gdrive";
        if (u.Contains("dropbox.com")) return "dropbox";
        if (u.Contains("catbox.moe")) return "catbox";
        if (u.Contains("bunkr.")) return "bunkr";

        if (!string.IsNullOrWhiteSpace(reportedHost)
            && !reportedHost.Equals("http", StringComparison.OrdinalIgnoreCase)
            && !reportedHost.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            && !reportedHost.Equals("skip", StringComparison.OrdinalIgnoreCase))
            return reportedHost.Trim().ToLowerInvariant();

        if (LooksLikeArchive(u)) return "http";
        if (u.StartsWith("http://") || u.StartsWith("https://"))
        {
            try { return new Uri(url).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase); }
            catch { return "http"; }
        }

        return "skip";
    }

    /// <summary>
    /// Infer Windows / Linux / Mac / Android / PC / Windows/Linux from URL or heading text.
    /// Ren'Py "PC" builds usually ship both Windows and Linux in one archive.
    /// </summary>
    public static string? InferPlatform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();

        if (ContainsAny(t,
                "windows/linux", "linux/windows", "win/linux", "linux/win",
                "windows / linux", "linux / windows", "windows & linux", "linux & windows",
                "windows and linux", "linux and windows", "win-linux", "windows-linux"))
            return "Windows/Linux";

        if (ContainsAny(t, "windows/mac", "win/mac", "windows / mac", "pc/mac", "pc / mac"))
            return "PC/Mac";

        var hasWin = ContainsWinToken(t);
        var hasLinux = t.Contains("linux", StringComparison.Ordinal);
        var hasMac = t.Contains("macos", StringComparison.Ordinal) || t.Contains("osx", StringComparison.Ordinal)
                     || t.Contains("-mac", StringComparison.Ordinal) || t.Contains("_mac", StringComparison.Ordinal)
                     || t.Contains("/mac/", StringComparison.Ordinal) || HasWord(t, "mac");
        var hasAndroid = t.Contains("android", StringComparison.Ordinal) || t.Contains("-apk", StringComparison.Ordinal)
                         || t.EndsWith(".apk", StringComparison.Ordinal);
        var hasIos = HasWord(t, "ios") || t.Contains("-ios", StringComparison.Ordinal)
                     || t.Contains("_ios", StringComparison.Ordinal) || t.Contains("/ios/", StringComparison.Ordinal);
        var hasPc = HasWord(t, "pc");

        if (hasPc && !hasMac && !hasAndroid && !hasIos)
            return hasLinux && !hasWin ? "PC" : "PC"; // dual Win+Linux package

        if (hasWin && hasLinux) return "Windows/Linux";
        if (hasWin) return "Windows";
        if (hasLinux) return "Linux";
        if (hasMac) return "Mac";
        if (hasAndroid) return "Android";
        if (hasIos) return "iOS";
        if (hasPc) return "PC";
        return null;
    }

    /// <summary>Normalize an already-scraped label (e.g. hub sent "pc").</summary>
    public static string? NormalizePlatformLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        return InferPlatform(label) ?? (label.Trim().Length < 40 ? label.Trim() : null);
    }

    /// <summary>True when a filter chip should include this platform label.</summary>
    public static bool MatchesFilter(string? platform, string filter)
    {
        if (string.Equals(filter, "All", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(filter, "Unknown", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(platform);

        var p = platform ?? "";
        if (string.Equals(filter, "Windows", StringComparison.OrdinalIgnoreCase))
            return p.Contains("Windows", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(p, "PC", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("Win/", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "Linux", StringComparison.OrdinalIgnoreCase))
            return p.Contains("Linux", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(p, "PC", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "Mac", StringComparison.OrdinalIgnoreCase))
            return p.Contains("Mac", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("OS X", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("OSX", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "Android", StringComparison.OrdinalIgnoreCase))
            return p.Contains("Android", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "iOS", StringComparison.OrdinalIgnoreCase))
            return p.Contains("iOS", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("IOS", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(filter, "PC", StringComparison.OrdinalIgnoreCase))
            return string.Equals(p, "PC", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("Windows/Linux", StringComparison.OrdinalIgnoreCase);

        return string.Equals(p, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWinToken(string t) =>
        t.Contains("windows", StringComparison.Ordinal)
        || t.Contains("-win", StringComparison.Ordinal)
        || t.Contains("_win", StringComparison.Ordinal)
        || t.Contains("/win/", StringComparison.Ordinal)
        || t.Contains(" win ", StringComparison.Ordinal)
        || HasWord(t, "win");

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(n => haystack.Contains(n, StringComparison.Ordinal));

    private static bool HasWord(string text, string word)
    {
        var idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            var after = idx + word.Length;
            var afterOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            if (beforeOk && afterOk) return true;
            idx += word.Length;
        }
        return false;
    }

    private static string? ExtractMaskedTargetHost(string lowerUrl)
    {
        const string marker = "f95zone.to/masked/";
        var idx = lowerUrl.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var rest = lowerUrl[(idx + marker.Length)..];
        var slash = rest.IndexOf('/');
        var host = slash >= 0 ? rest[..slash] : rest;
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    private static string? CleanSectionTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title.Trim();
        if (t.Length < 2 || t.Length > 80) return null;
        if (LooksLikeOpaqueId(t)) return null;
        // Drop bare platform-only leftovers that slipped through.
        if (InferPlatform(t) is not null
            && !t.Contains(' ', StringComparison.Ordinal)
            && !t.Contains('-', StringComparison.Ordinal)
            && t.Length < 20)
            return null;
        return t;
    }

    private static string BuildDisplayName(string url, string host, string? platform, string? title, string? label)
    {
        try
        {
            // Prefer platform (or host) as the row label — pack title is shown as a group header in UI.
            if (platform is not null)
                return platform;

            if (!string.IsNullOrWhiteSpace(title)
                && !title.Equals("Extras", StringComparison.OrdinalIgnoreCase)
                && !title.Equals("Extra", StringComparison.OrdinalIgnoreCase))
                return title.Trim();

            if (url.Contains("f95zone.to/masked/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("f95zone.to/masked-navigation", StringComparison.OrdinalIgnoreCase))
                return $"Masked {host}";

            if (!string.IsNullOrWhiteSpace(label) && label.Length < 80 && !LooksLikeOpaqueId(label)
                && InferPlatform(label) is null)
                return label.Trim();

            var uri = new Uri(url);
            var file = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(file) && file.Contains('.') && !LooksLikeOpaqueId(file))
                return Uri.UnescapeDataString(file);

            return host;
        }
        catch
        {
            return host;
        }
    }

    private static bool LooksLikeOpaqueId(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return true;

        // Space-joined opaque hoster path segments (masked pixeldrain titles).
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2 && parts.All(LooksLikeOpaqueToken))
            return true;

        if (LooksLikeOpaqueToken(v))
            return true;

        if (v.Length >= 24 && v.Count(c => c is '.' or '-' or '_') >= 2 && !v.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !v.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            && !v.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            && !v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return true;
        if (v.Length > 40 && v.Count(char.IsLetterOrDigit) > v.Length * 0.85)
            return true;
        return false;
    }

    private static bool LooksLikeOpaqueToken(string value)
    {
        var v = value.Trim();
        if (v.Length < 10) return false;
        if (v.Contains(".zip", StringComparison.OrdinalIgnoreCase)
            || v.Contains(".rar", StringComparison.OrdinalIgnoreCase)
            || v.Contains(".7z", StringComparison.OrdinalIgnoreCase)
            || v.Contains(".apk", StringComparison.OrdinalIgnoreCase))
            return false;
        var alnum = v.Count(char.IsLetterOrDigit);
        return alnum >= v.Length * 0.85 && !v.Contains(' ');
    }

    private static bool IsJunk(string u) =>
        u.EndsWith(".css") || u.Contains(".css?") ||
        u.EndsWith(".js") || u.Contains(".js?") ||
        u.Contains("/css/") || u.Contains("/js/") ||
        u.Contains("fontawesome") || u.Contains("cdnjs.") ||
        u.Contains("f95zone.to/threads/") ||
        u.Contains("f95zone.to/members/") ||
        u.Contains("f95zone.to/styles/") ||
        u.Contains("f95zone.to/data/");

    private static bool LooksLikeArchive(string u) =>
        u.EndsWith(".zip") || u.Contains(".zip?") ||
        u.EndsWith(".rar") || u.Contains(".rar?") ||
        u.EndsWith(".7z") || u.Contains(".7z?") ||
        u.EndsWith(".exe") || u.Contains(".exe?");
}
