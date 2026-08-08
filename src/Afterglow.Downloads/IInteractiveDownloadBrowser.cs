namespace Afterglow.Downloads;

/// <summary>
/// Opens Afterglow Browser for captcha/timer/interstitial hosters.
/// When a real file download starts, returns a <see cref="BrowserHandoff"/> so
/// DownloadManager can fetch it with progress — the WebView does not keep the file.
/// </summary>
public interface IInteractiveDownloadBrowser
{
    /// <param name="seedCookieHeader">Optional Cookie header (e.g. F95 session) seeded before navigation.</param>
    /// <param name="seedCookieDomain">Cookie domain (e.g. .f95zone.to).</param>
    Task<BrowserHandoff?> CaptureDownloadAsync(
        Uri url,
        string? seedCookieHeader = null,
        string? seedCookieDomain = null,
        CancellationToken cancellationToken = default);
}
