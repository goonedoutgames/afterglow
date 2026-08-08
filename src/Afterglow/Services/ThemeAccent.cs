using Avalonia;
using Avalonia.Media;
using Afterglow.Core.Models;

namespace Afterglow.Services;

public static class ThemeAccent
{
    public static void Apply(string? hex)
    {
        if (!TryParseColor(hex, out var color))
            color = Color.Parse(UiPreferences.DefaultAccentHex);

        if (Application.Current?.Resources is not { } resources)
            return;

        resources["AccentColor"] = color;
        resources["AccentBrush"] = new SolidColorBrush(color);
        resources["AccentGlowBrush"] = new SolidColorBrush(Color.FromArgb(0xCC, color.R, color.G, color.B));
        resources["AccentBloomBrush"] = new SolidColorBrush(Color.FromArgb(0x66, color.R, color.G, color.B));
        resources["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B));

        // Host-level hover bloom (BoxShadows resource — string DynamicResource mid-value does not work).
        var rim = Color.FromArgb(0xD0, color.R, color.G, color.B);
        var bloom = Color.FromArgb(0x70, color.R, color.G, color.B);
        resources["CardHoverGlow"] = BoxShadows.Parse(
            $"0 0 0 1.5 #{rim.A:X2}{rim.R:X2}{rim.G:X2}{rim.B:X2}, " +
            $"0 0 28 4 #{bloom.A:X2}{bloom.R:X2}{bloom.G:X2}{bloom.B:X2}, " +
            "0 14 36 0 #66000000");
    }

    public static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        var value = hex.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;
        try
        {
            color = Color.Parse(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToHex(Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
