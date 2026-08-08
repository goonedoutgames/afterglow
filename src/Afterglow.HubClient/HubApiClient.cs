using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Afterglow.Core.Models;

namespace Afterglow.HubClient;

/// <summary>Typed HTTP client for an AVN Hub instance. Base URL and token are reconfigurable after requests.</summary>
public sealed class HubApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private Uri _baseAddress;
    private string? _bearerToken;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public Uri BaseAddress
    {
        get => _baseAddress;
        set => _baseAddress = NormalizeBase(value);
    }

    public string? BearerToken
    {
        get => _bearerToken;
        set => _bearerToken = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public HubApiClient(Uri baseAddress, string? bearerToken = null, HttpClient? httpClient = null)
    {
        // Never set HttpClient.BaseAddress — it becomes immutable after the first request.
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _ownsClient = httpClient is null;
        _baseAddress = NormalizeBase(baseAddress);
        BearerToken = bearerToken;
    }

    public void Configure(Uri baseAddress, string? bearerToken = null)
    {
        BaseAddress = baseAddress;
        BearerToken = bearerToken;
    }

    public async Task<bool> HealthAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync<object, HealthResponse>(HttpMethod.Get, "api/v1/health", null, cancellationToken)).Ok;

    public async Task<TokenResponse> LoginAsync(string password, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<object, TokenResponse>(HttpMethod.Post, "api/v1/auth/login",
            new { password }, cancellationToken);
        BearerToken = result.Token;
        return result;
    }

    public Task<AuthMe> MeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, AuthMe>(HttpMethod.Get, "api/v1/auth/me", null, cancellationToken);

    public Task<List<GameSummary>> GetLibraryAsync(
        string? query = null,
        string? playStatus = null,
        string? sort = null,
        string? platforms = null,
        string? tags = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<object, List<GameSummary>>(HttpMethod.Get, WithQuery(
            "api/v1/library",
            ("search", query),
            ("play_status", playStatus),
            ("sort", sort),
            ("platforms", platforms),
            ("tags", tags)), null, cancellationToken);

    public Task<List<LibraryTag>> GetLibraryTagsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, List<LibraryTag>>(HttpMethod.Get, "api/v1/library/tags", null, cancellationToken);

    public Task<List<CatalogTag>> GetCatalogTagsAsync(string? query = null, int limit = 200, CancellationToken cancellationToken = default) =>
        SendAsync<object, List<CatalogTag>>(HttpMethod.Get, WithQuery(
            "api/v1/catalog/tags",
            ("q", query),
            ("limit", limit.ToString())), null, cancellationToken);

    public Task<GameDetail> GetGameAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendAsync<object, GameDetail>(HttpMethod.Get, $"api/v1/games/{gameId}", null, cancellationToken);

    public Task<GameDetail> AddGameAsync(string input, CancellationToken cancellationToken = default) =>
        SendAsync<AddGameRequest, GameDetail>(HttpMethod.Post, "api/v1/library/add", new AddGameRequest { Input = input }, cancellationToken);

    public Task<GameDetail> RefreshGameAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendAsync<object, GameDetail>(HttpMethod.Post, $"api/v1/games/{gameId}/refresh", null, cancellationToken);

    public Task<GameDetail> SetCoverFromScreenshotAsync(long gameId, int screenshotIndex, CancellationToken cancellationToken = default) =>
        SendAsync<SetCoverRequest, GameDetail>(HttpMethod.Post, $"api/v1/games/{gameId}/cover",
            new SetCoverRequest { ScreenshotIndex = screenshotIndex }, cancellationToken);

    public Task<GameDetail> ResetCoverAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendAsync<object, GameDetail>(HttpMethod.Post, $"api/v1/games/{gameId}/cover/reset", null, cancellationToken);

    public async Task<GameDetail> UpdateGameAsync(long gameId, UpdateGameUserData update, CancellationToken cancellationToken = default)
    {
        // Hub treats omitted user_rating as "unchanged" and JSON null as "clear".
        using var doc = JsonSerializer.SerializeToDocument(update, JsonOptions);
        var root = doc.RootElement.Clone();
        using var stream = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("clear_user_rating")) continue;
                prop.WriteTo(writer);
            }
            if (update.ClearUserRating == true)
                writer.WriteNull("user_rating");
            writer.WriteEndObject();
        }

        using var request = CreateRequest(HttpMethod.Patch, $"api/v1/games/{gameId}");
        request.Content = new ByteArrayContent(stream.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadResponseAsync<GameDetail>(response, cancellationToken);
    }

    public Task<List<F95SearchResult>> CatalogSearchAsync(
        string? query = null,
        int? page = null,
        string? sort = null,
        int? dateDays = null,
        string? creator = null,
        string? tags = null,
        string? notags = null,
        string? tagMode = null,
        string? prefixes = null,
        CancellationToken cancellationToken = default) =>
        SendAsync<object, List<F95SearchResult>>(HttpMethod.Get, WithQuery(
            "api/v1/catalog/search",
            ("q", query),
            ("creator", creator),
            ("page", page?.ToString()),
            ("sort", sort),
            ("date", dateDays is > 0 ? dateDays.Value.ToString() : null),
            ("tags", tags),
            ("notags", notags),
            ("tag_mode", tagMode),
            ("prefixes", prefixes)), null, cancellationToken);

    public Uri ResolveUri(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL is empty.", nameof(url));
        // Hub on Windows historically emitted `/api/v1/media/123\ss_0.jpg` — normalize.
        url = url.Trim().Replace('\\', '/');
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute;
        return new Uri(_baseAddress, url.TrimStart('/'));
    }

    public async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken cancellationToken = default)
    {
        var (bytes, _, _) = await DownloadBytesDetailedAsync(url, cancellationToken);
        return bytes;
    }

    public async Task<(byte[]? Bytes, string? Error, int? Status)> DownloadBytesDetailedAsync(
        string url, CancellationToken cancellationToken = default)
    {
        try
        {
            var uri = ResolveUri(url);
            if (IsHubMedia(uri) && !string.IsNullOrWhiteSpace(BearerToken))
            {
                var sep = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
                if (!uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase))
                    uri = new Uri(uri + sep + "token=" + Uri.EscapeDataString(BearerToken));
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyAuth(request);
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            // Prefer formats Avalonia can decode; AVIF still converted via Magick when hub already cached it.
            request.Headers.TryAddWithoutValidation("Accept", "image/webp,image/jpeg,image/png,image/gif,image/*,*/*;q=0.8");
            // F95 attachment CDN often 403s without a forum referer.
            if (uri.Host.Contains("f95zone", StringComparison.OrdinalIgnoreCase))
                request.Headers.Referrer = new Uri("https://f95zone.to/");
            else
                request.Headers.Referrer = null;

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var snippet = body.Length > 120 ? body[..120] : body;
                return (null, $"HTTP {status} for {uri.Host}{uri.AbsolutePath}: {snippet}", status);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return (bytes, null, status);
        }
        catch (Exception ex)
        {
            return (null, ex.Message, null);
        }
    }

    public async Task<Stream> OpenStreamAsync(string url, CancellationToken cancellationToken = default)
    {
        var (bytes, error, _) = await DownloadBytesDetailedAsync(url, cancellationToken);
        if (bytes is null)
            throw new HubApiException(System.Net.HttpStatusCode.NotFound, error ?? "Media download failed.");
        return new MemoryStream(bytes, writable: false);
    }

    public Task<SettingsView> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, SettingsView>(HttpMethod.Get, "api/v1/settings", null, cancellationToken);

    public Task<SettingsView> UpdateSettingsAsync(UpdateSettingsRequest settings, CancellationToken cancellationToken = default) =>
        SendAsync<UpdateSettingsRequest, SettingsView>(HttpMethod.Put, "api/v1/settings", settings, cancellationToken);

    public Task<F95AuthResult> F95LoginAsync(string username, string password, CancellationToken cancellationToken = default) =>
        SendAsync<F95LoginRequest, F95AuthResult>(HttpMethod.Post, "api/v1/settings/f95/login",
            new F95LoginRequest { Username = username, Password = password }, cancellationToken);

    public Task<F95AuthResult> F95CookiesAsync(string cookies, CancellationToken cancellationToken = default) =>
        SendAsync<F95CookiesRequest, F95AuthResult>(HttpMethod.Post, "api/v1/settings/f95/cookies",
            new F95CookiesRequest { Cookies = cookies }, cancellationToken);

    public Task<F95CookiesExport> GetF95CookiesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, F95CookiesExport>(HttpMethod.Get, "api/v1/settings/f95/cookies", null, cancellationToken);

    public Task<StorageStats> GetStorageAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, StorageStats>(HttpMethod.Get, "api/v1/settings/storage", null, cancellationToken);

    public Task<OkResult> PurgeMediaAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, OkResult>(HttpMethod.Post, "api/v1/settings/media/purge", null, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        SendNoContentAsync<object>(HttpMethod.Post, "api/v1/auth/logout", null, cancellationToken);

    public Task DeleteGameAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync<object>(HttpMethod.Delete, $"api/v1/games/{gameId}", null, cancellationToken);

    public Task<VersionCheckResult> CheckVersionAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendAsync<object, VersionCheckResult>(HttpMethod.Post, $"api/v1/games/{gameId}/check-version", null, cancellationToken);

    public Task<List<VersionCheckResult>> CheckAllUpdatesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, List<VersionCheckResult>>(HttpMethod.Post, "api/v1/library/check-updates", null, cancellationToken);

    public Task<List<DownloadLink>> GetDownloadLinksAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendAsync<object, List<DownloadLink>>(HttpMethod.Get, $"api/v1/games/{gameId}/download-links", null, cancellationToken);

    public Task<PlaytimeSummary> GetPlaytimeAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendAsync<object, PlaytimeSummary>(HttpMethod.Get, $"api/v1/games/{gameId}/playtime", null, cancellationToken);

    public Task PostPlaytimeAsync(long gameId, IEnumerable<PlaySessionDto> sessions, CancellationToken cancellationToken = default) =>
        SendNoContentAsync(HttpMethod.Post, $"api/v1/games/{gameId}/playtime",
            new PlaytimeBatchRequest { Sessions = sessions.ToList() }, cancellationToken);

    public async Task<GameSave> UploadSaveAsync(long gameId, string filePath, string? uploadName = null, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        using var form = new MultipartFormDataContent();
        using var file = new StreamContent(stream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", uploadName ?? Path.GetFileName(filePath));
        using var request = CreateRequest(HttpMethod.Post, $"api/v1/games/{gameId}/saves");
        request.Content = form;
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadResponseAsync<GameSave>(response, cancellationToken);
    }

    public Task<List<GameSave>> ListSavesAsync(long gameId, CancellationToken cancellationToken = default) =>
        SendAsync<object, List<GameSave>>(HttpMethod.Get, $"api/v1/games/{gameId}/saves", null, cancellationToken);

    public Uri DownloadSaveUri(long gameId, long saveId) =>
        ResolveUri($"api/v1/games/{gameId}/saves/{saveId}");

    public Task<byte[]?> DownloadSaveAsync(long gameId, long saveId, CancellationToken cancellationToken = default) =>
        DownloadBytesAsync($"api/v1/games/{gameId}/saves/{saveId}", cancellationToken);

    public Task DeleteSaveAsync(long gameId, long saveId, CancellationToken cancellationToken = default) =>
        SendNoContentAsync<object>(HttpMethod.Delete, $"api/v1/games/{gameId}/saves/{saveId}", null, cancellationToken);

    private async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string path, TRequest? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadResponseAsync<TResponse>(response, cancellationToken);
    }

    private async Task SendNoContentAsync<TRequest>(HttpMethod method, string path, TRequest? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) await ThrowHubErrorAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var uri = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                  || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(path)
            : new Uri(_baseAddress, path.TrimStart('/'));
        var request = new HttpRequestMessage(method, uri);
        ApplyAuth(request);
        return request;
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
    }

    private bool IsHubMedia(Uri uri)
    {
        try
        {
            return uri.Host.Equals(_baseAddress.Host, StringComparison.OrdinalIgnoreCase)
                   && uri.AbsolutePath.Contains("/api/v1/media/", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static Uri NormalizeBase(Uri value) =>
        new(value.AbsoluteUri.TrimEnd('/') + "/");

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) await ThrowHubErrorAsync(response, cancellationToken);
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return value ?? throw new HubApiException(response.StatusCode, "The server returned an empty response.");
    }

    private static async Task ThrowHubErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var message = await response.Content.ReadAsStringAsync(cancellationToken);
        try { message = JsonSerializer.Deserialize<HubError>(message, JsonOptions)?.Error ?? message; } catch (JsonException) { }
        throw new HubApiException(response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "Hub request failed." : message);
    }

    private static string WithQuery(string path, params (string Name, string? Value)[] values)
    {
        var q = string.Join("&", values.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Name)}={Uri.EscapeDataString(x.Value!)}"));
        return string.IsNullOrEmpty(q) ? path : $"{path}?{q}";
    }

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}

internal sealed class HealthResponse
{
    public bool Ok { get; set; }
}

public sealed class HubApiException(System.Net.HttpStatusCode statusCode, string message) : Exception(message)
{
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
