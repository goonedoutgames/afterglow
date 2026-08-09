using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Afterglow.Core;

namespace Afterglow.Services;

public sealed record AppUpdateInfo(string TagName, string Version, string HtmlUrl, string? Name);

/// <summary>Checks goonedoutgames/afterglow GitHub Releases for a newer semver tag.</summary>
public sealed class GitHubReleaseUpdateChecker
{
    public const string DefaultOwner = "goonedoutgames";
    public const string DefaultRepo = "afterglow";
    public const string ReleasesUrl = "https://github.com/goonedoutgames/afterglow/releases";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubReleaseUpdateChecker(HttpClient? httpClient = null, string owner = DefaultOwner, string repo = DefaultRepo)
    {
        _owner = owner;
        _repo = repo;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Afterglow", AppVersionInfo.Current));
        if (!_http.DefaultRequestHeaders.Accept.Any())
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<AppUpdateInfo?> CheckAsync(string? currentVersion = null, string? ignoredVersion = null, CancellationToken ct = default)
    {
        var current = currentVersion ?? AppVersionInfo.Current;
        if (!AppVersionInfo.IsReleaseBuild && currentVersion is null)
            return null; // local/dev/CI prerelease builds shouldn't nag

        if (!SemVer.TryParse(current, out _, out var curPre) || curPre)
            return null;

        var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, JsonOptions, ct);
        if (release is null || release.Draft || release.Prerelease || string.IsNullOrWhiteSpace(release.TagName))
            return null;

        var remote = SemVer.Normalize(release.TagName);
        if (!SemVer.IsNewerStable(current, remote))
            return null;

        var ignored = SemVer.Normalize(ignoredVersion);
        if (!string.IsNullOrEmpty(ignored) &&
            string.Equals(ignored, remote, StringComparison.OrdinalIgnoreCase))
            return null;

        var html = string.IsNullOrWhiteSpace(release.HtmlUrl)
            ? $"{ReleasesUrl}/tag/{release.TagName}"
            : release.HtmlUrl!;

        return new AppUpdateInfo(release.TagName!, remote, html, release.Name);
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
    }
}
