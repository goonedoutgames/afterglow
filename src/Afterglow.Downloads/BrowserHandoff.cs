namespace Afterglow.Downloads;

/// <summary>
/// Direct download URL captured from Afterglow Browser — handed off to DownloadManager
/// so progress lives in-app and the WebView can close immediately.
/// </summary>
public sealed class BrowserHandoff
{
    public required Uri DirectUrl { get; init; }
    public string? CookieHeader { get; init; }
    public string? Referer { get; init; }
    public string? UserAgent { get; init; }
    public string? SuggestedFileName { get; init; }
}
