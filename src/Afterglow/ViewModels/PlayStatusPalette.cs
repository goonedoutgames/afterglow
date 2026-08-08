using Avalonia.Media;

namespace Afterglow.ViewModels;

/// <summary>Shared play-status colors aligned with the hub web badges.</summary>
public static class PlayStatusPalette
{
    public static string Label(string? status) => Normalize(status) switch
    {
        "playing" => "Playing",
        "completed" => "Completed",
        "dropped" => "Dropped",
        "on_hold" => "On hold",
        _ => "Unplayed"
    };

    public static string Normalize(string? status)
    {
        var s = (status ?? "unplayed").Trim().ToLowerInvariant();
        if (s is "on-hold") return "on_hold";
        return s;
    }

    public static IBrush Fill(string? status) => Normalize(status) switch
    {
        "playing" => Brush("#2A5F9E"),
        "completed" => Brush("#1F6B45"),
        "dropped" => Brush("#8A3040"),
        "on_hold" => Brush("#6B5A2A"),
        _ => Brush("#3A4658")
    };

    public static IBrush Border(string? status) => Normalize(status) switch
    {
        "playing" => Brush("#4A8FD4"),
        "completed" => Brush("#3D9A66"),
        "dropped" => Brush("#C45A6A"),
        "on_hold" => Brush("#C4A24A"),
        _ => Brush("#5A6A7E")
    };

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));
}
