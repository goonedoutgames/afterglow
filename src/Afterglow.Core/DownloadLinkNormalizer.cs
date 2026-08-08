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

        var platform = InferPlatform(url) ?? InferPlatform(link.Label) ?? null;
        var display = BuildDisplayName(url, host, platform, link.Label);
        return new NormalizedLink(url, host, platform, display, isMasked, isMasked ? url : null);
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
            // Keep unknown hosters that survived the hub scrape.
            try { return new Uri(url).Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase); }
            catch { return "http"; }
        }

        return "skip";
    }

    public static string? InferPlatform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.ToLowerInvariant();
        if (t.Contains("-win") || t.Contains("_win") || t.Contains("windows") || t.Contains("/win/") || t.Contains(" win "))
            return "Windows";
        if (t.Contains("-linux") || t.Contains("_linux") || t.Contains("linux") || t.Contains("/linux/"))
            return "Linux";
        if (t.Contains("-mac") || t.Contains("_mac") || t.Contains("macos") || t.Contains("osx") || t.Contains("/mac/"))
            return "Mac";
        if (t.Contains("android") || t.Contains("-apk") || t.EndsWith(".apk"))
            return "Android";
        return null;
    }

    private static string? ExtractMaskedTargetHost(string lowerUrl)
    {
        // https://f95zone.to/masked/pixeldrain.com/177954/...
        const string marker = "f95zone.to/masked/";
        var idx = lowerUrl.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var rest = lowerUrl[(idx + marker.Length)..];
        var slash = rest.IndexOf('/');
        var host = slash >= 0 ? rest[..slash] : rest;
        return string.IsNullOrWhiteSpace(host) ? null : host;
    }

    private static string BuildDisplayName(string url, string host, string? platform, string? label)
    {
        try
        {
            // Masked F95 paths end in opaque ids that look like "filenames" — never show those.
            if (url.Contains("f95zone.to/masked/", StringComparison.OrdinalIgnoreCase)
                || url.Contains("f95zone.to/masked-navigation", StringComparison.OrdinalIgnoreCase))
            {
                var masked = $"Masked {host}";
                return platform is null ? masked : $"{masked} · {platform}";
            }

            if (!string.IsNullOrWhiteSpace(label) && label.Length < 80 && !LooksLikeOpaqueId(label))
                return platform is null ? label.Trim() : $"{platform} · {label.Trim()}";

            var uri = new Uri(url);
            var file = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(file) && file.Contains('.') && !LooksLikeOpaqueId(file))
            {
                var name = Uri.UnescapeDataString(file);
                return platform is null ? name : $"{platform} · {name}";
            }

            return platform is null ? host : $"{host} · {platform}";
        }
        catch
        {
            return host;
        }
    }

    private static bool LooksLikeOpaqueId(string value)
    {
        var v = value.Trim();
        if (v.Length >= 24 && v.Count(c => c is '.' or '-' or '_') >= 2 && !v.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            && !v.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            && !v.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            && !v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return true;
        // mega / pixeldrain style path segments
        if (v.Length > 40 && v.Count(char.IsLetterOrDigit) > v.Length * 0.85)
            return true;
        return false;
    }

    private static bool IsJunk(string u) =>
        u.EndsWith(".css") || u.Contains(".css?") ||
        u.EndsWith(".js") || u.Contains(".js?") ||
        u.Contains("/css/") || u.Contains("/js/") ||
        u.Contains("fontawesome") || u.Contains("cdnjs.") ||
        u.Contains("f95zone.to/threads/") ||
        u.Contains("f95zone.to/members/") ||
        u.Contains("f95zone.to/styles/") ||
        u.Contains("f95zone.to/data/") ||
        u.Contains("attachments.f95zone.to");

    private static bool LooksLikeArchive(string u) =>
        u.EndsWith(".zip") || u.Contains(".zip?") ||
        u.EndsWith(".rar") || u.Contains(".rar?") ||
        u.EndsWith(".7z") || u.Contains(".7z?") ||
        u.EndsWith(".exe") || u.Contains(".exe?");
}
