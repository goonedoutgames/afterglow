using System.Globalization;
using System.Text.RegularExpressions;

namespace Afterglow.Core;

/// <summary>Lightweight semver compare for app update checks (major.minor.patch[+prerelease ignored for ordering of releases]).</summary>
public static class SemVer
{
    private static readonly Regex CorePattern = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?<pre>[-+][0-9A-Za-z.\-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..].Trim();
        // AssemblyInformationalVersion may append "+commit"
        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];
        return s;
    }

    public static bool TryParse(string? raw, out (int Major, int Minor, int Patch) version, out bool isPrerelease)
    {
        version = default;
        isPrerelease = false;
        var s = Normalize(raw);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = CorePattern.Match(s);
        if (!m.Success) return false;
        version = (
            int.Parse(m.Groups["major"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["minor"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["patch"].Value, CultureInfo.InvariantCulture));
        isPrerelease = m.Groups["pre"].Success && m.Groups["pre"].Value.StartsWith('-');
        return true;
    }

    /// <summary>Returns true when <paramref name="candidate"/> is a newer stable release than <paramref name="current"/>.</summary>
    public static bool IsNewerStable(string? current, string? candidate)
    {
        if (!TryParse(current, out var cur, out _)) return false;
        if (!TryParse(candidate, out var next, out var nextPre)) return false;
        if (nextPre) return false;
        var cmp = cur.Major != next.Major ? cur.Major.CompareTo(next.Major)
            : cur.Minor != next.Minor ? cur.Minor.CompareTo(next.Minor)
            : cur.Patch.CompareTo(next.Patch);
        return cmp < 0;
    }
}
