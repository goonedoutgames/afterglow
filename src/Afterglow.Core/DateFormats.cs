using System.Globalization;

namespace Afterglow.Core;

/// <summary>Human-readable dates for cloud saves and similar UI (local time).</summary>
public static class DateFormats
{
    /// <summary>Example: January 30th 2026 11:49 PM</summary>
    public static string FormatFriendly(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
            && !DateTimeOffset.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.AssumeUniversal, out dto))
            return raw.Trim();

        var local = dto.ToLocalTime();
        var day = local.Day;
        var suffix = day is 11 or 12 or 13
            ? "th"
            : (day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            };
        return $"{local:MMMM} {day}{suffix} {local:yyyy} {local:h:mm tt}";
    }
}
