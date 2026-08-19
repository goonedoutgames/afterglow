using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Afterglow.Core;

namespace Afterglow.Services;

public sealed record AppUpdateInfo(
    string TagName,
    string Version,
    string HtmlUrl,
    string? Name,
    string? InstallerUrl);

/// <summary>
/// Finds a newer stable Afterglow GitHub Release by semver (not GitHub's "latest" flag).
/// Falls back to the public Atom feed when the API is slow or rate-limited.
/// </summary>
public sealed class GitHubReleaseUpdateChecker : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GitHubReleaseUpdateChecker(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _ownsHttp = true;
            _http = CreateClient();
        }
        else
        {
            _ownsHttp = false;
            _http = httpClient;
            EnsureHeaders(_http);
        }
    }

    public static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            AllowAutoRedirect = true
        })
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        EnsureHeaders(client);
        return client;
    }

    private static void EnsureHeaders(HttpClient http)
    {
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                $"Afterglow/{AppVersionInfo.Current} (+https://github.com/{AppUpdateAssets.Owner}/{AppUpdateAssets.Repo})");
        if (http.DefaultRequestHeaders.Accept.Count == 0)
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        if (!http.DefaultRequestHeaders.Contains("X-GitHub-Api-Version"))
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public HttpClient Client => _http;

    public string? LastError { get; private set; }

    public async Task<AppUpdateInfo?> CheckAsync(
        string? currentVersion = null,
        string? ignoredVersion = null,
        CancellationToken ct = default)
    {
        LastError = null;
        var current = currentVersion ?? AppVersionInfo.Current;
        if (currentVersion is null && !AppVersionInfo.IsReleaseBuild)
        {
            LastError = $"Dev build {current} — install a GitHub release to receive updates.";
            return null;
        }

        if (!SemVer.TryParse(current, out _, out var curPre) || curPre)
        {
            LastError = $"Current version '{current}' is not a stable semver.";
            return null;
        }

        Exception? lastError = null;
        AppUpdateInfo? found = null;
        var apiOk = false;
        try
        {
            found = await CheckViaApiAsync(current, ct);
            apiOk = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lastError = ex;
        }

        if (found is null && !apiOk)
        {
            try
            {
                found = await CheckViaAtomAsync(current, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError ??= ex;
            }
        }

        if (found is null)
        {
            if (lastError is not null)
            {
                LastError = lastError.Message;
                System.Diagnostics.Debug.WriteLine($"Afterglow update check failed: {lastError.Message}");
            }
            return null;
        }

        var ignored = SemVer.Normalize(ignoredVersion);
        if (!string.IsNullOrEmpty(ignored) &&
            string.Equals(ignored, found.Version, StringComparison.OrdinalIgnoreCase))
            return null;

        return found;
    }

    private async Task<AppUpdateInfo?> CheckViaApiAsync(string current, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{AppUpdateAssets.Owner}/{AppUpdateAssets.Repo}/releases?per_page=20";
        using var resp = await SendWithRetryAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub releases API HTTP {(int)resp.StatusCode}");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubReleaseDto>>(stream, JsonOptions, ct)
                       ?? [];

        GitHubReleaseDto? best = null;
        string? bestVersion = null;
        foreach (var release in releases)
        {
            if (release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
                continue;
            var version = SemVer.Normalize(release.TagName);
            if (!SemVer.IsNewerStable(current, version)) continue;
            if (bestVersion is null || SemVer.IsNewerStable(bestVersion, version))
            {
                best = release;
                bestVersion = version;
            }
        }

        if (best is null || bestVersion is null) return null;
        return ToInfo(best, bestVersion);
    }

    private async Task<AppUpdateInfo?> CheckViaAtomAsync(string current, CancellationToken ct)
    {
        var url = $"https://github.com/{AppUpdateAssets.Owner}/{AppUpdateAssets.Repo}/releases.atom";
        using var resp = await SendWithRetryAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub releases feed HTTP {(int)resp.StatusCode}");

        var xml = await resp.Content.ReadAsStringAsync(ct);
        var tags = ParseAtomTags(xml);
        var newest = AppUpdateAssets.PickNewestNewerStable(tags, current);
        if (newest is null) return null;

        var version = SemVer.Normalize(newest);
        var tag = newest.StartsWith('v') || newest.StartsWith('V') ? newest : "v" + version;
        return new AppUpdateInfo(
            tag,
            version,
            $"{AppUpdateAssets.ReleasesUrl}/tag/{tag}",
            null,
            AppUpdateAssets.DirectInstallerUrl(tag));
    }

    internal static IReadOnlyList<string> ParseAtomTags(string xml)
    {
        var tags = new List<string>();
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://www.w3.org/2005/Atom";
            var titles = doc.Descendants(ns + "entry").Select(e => e.Element(ns + "title")?.Value)
                .Concat(doc.Descendants("entry").Select(e => e.Element("title")?.Value));
            foreach (var title in titles)
            {
                if (string.IsNullOrWhiteSpace(title)) continue;
                var match = System.Text.RegularExpressions.Regex.Match(
                    title,
                    @"v?\d+\.\d+\.\d+",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    tags.Add(match.Value);
            }
        }
        catch
        {
            // ignore malformed feed
        }
        return tags;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string url, CancellationToken ct)
    {
        HttpRequestException? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode is 502 or 503 or 429 && attempt == 0)
                {
                    resp.Dispose();
                    await Task.Delay(400, ct);
                    continue;
                }
                return resp;
            }
            catch (HttpRequestException ex) when (attempt == 0)
            {
                last = ex;
                await Task.Delay(400, ct);
            }
        }
        throw last ?? new HttpRequestException("GitHub request failed.");
    }

    private static AppUpdateInfo ToInfo(GitHubReleaseDto release, string version)
    {
        var assets = (release.Assets ?? [])
            .Select(a => (a.Name ?? "", a.BrowserDownloadUrl ?? ""));
        var installer = AppUpdateAssets.PickWindowsInstallerUrl(assets)
                        ?? AppUpdateAssets.DirectInstallerUrl(release.TagName!);
        var html = string.IsNullOrWhiteSpace(release.HtmlUrl)
            ? $"{AppUpdateAssets.ReleasesUrl}/tag/{release.TagName}"
            : release.HtmlUrl!;
        return new AppUpdateInfo(release.TagName!, version, html, release.Name, installer);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
