namespace Afterglow.Downloads;

/// <summary>Stub used on non-Windows builds — captcha/timer hosters need Afterglow Browser (WebView2).</summary>
public sealed class UnsupportedInteractiveDownloadBrowser : IInteractiveDownloadBrowser
{
    public Task<BrowserHandoff?> CaptureDownloadAsync(
        Uri url,
        string? seedCookieHeader = null,
        string? seedCookieDomain = null,
        CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException(
            "Afterglow Browser (interactive hoster downloads) requires Windows with the WebView2 Runtime. " +
            "On Linux, use Remote hub mode and open hoster links in an external browser, or queue direct download URLs.");
}
