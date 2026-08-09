using System.Text.Json.Serialization;

namespace Afterglow.Core.Models;

public sealed class Game
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string? SourceTitle { get; set; }
    public bool TitleCustom { get; set; }
    public long? F95ThreadId { get; set; }
    public string? F95Url { get; set; }
    public string? Version { get; set; }
    public string? Developer { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<string> Platforms { get; set; } = [];
    public string? Description { get; set; }
    public string? CoverImagePath { get; set; }
    public double? Rating { get; set; }
    public string? Status { get; set; }
    public string? PlayStatus { get; set; }
    public double? UserRating { get; set; }
    public string? UserNotes { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
    public long PlaytimeSeconds { get; set; }
}

public sealed class GameSummary
{
    public Game Game { get; set; } = new();
    public string? CoverUrl { get; set; }
    public List<string> PreviewUrls { get; set; } = [];
}

public sealed class ScreenshotItem
{
    public string FullUrl { get; set; } = "";
    public string? CachedUrl { get; set; }
}

public sealed class GameSave
{
    public long Id { get; set; }
    public long GameId { get; set; }
    public string Path { get; set; } = "";
    public string Filename { get; set; } = "";
    public long Size { get; set; }
    public string UploadedAt { get; set; } = "";
}

public sealed class GamePatch
{
    public long Id { get; set; }
    public long GameId { get; set; }
    public string Path { get; set; } = "";
    public string Filename { get; set; } = "";
    public long Size { get; set; }
    public string? Description { get; set; }
    public string UploadedAt { get; set; } = "";
}

public sealed class GameDetail
{
    public Game Game { get; set; } = new();
    public string? CoverUrl { get; set; }
    public string? CoverFullUrl { get; set; }
    public List<ScreenshotItem> Screenshots { get; set; } = [];
    public bool IsCustomCover { get; set; }
    public List<GameSave> Saves { get; set; } = [];
    public List<GamePatch> Patches { get; set; } = [];
}

public sealed class CatalogPage
{
    public List<F95SearchResult> Items { get; set; } = [];
    public int Page { get; set; }
    public int TotalPages { get; set; }
    public int Rows { get; set; }
    public bool HasMore { get; set; }
}

public sealed class F95SearchResult
{
    public long ThreadId { get; set; }
    public string Title { get; set; } = "";
    public string Creator { get; set; } = "";
    public string Version { get; set; } = "";
    public string Cover { get; set; } = "";
    public List<string> Screenshots { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Prefixes { get; set; } = [];
    public List<string> Platforms { get; set; } = [];
    public double Rating { get; set; }
    public long? Likes { get; set; }
    public long? Views { get; set; }
    public string Url { get; set; } = "";
    public string Date { get; set; } = "";
    /// <summary>Present on catalog preview responses (thread overview).</summary>
    public string? Description { get; set; }
    public bool InLibrary { get; set; }
    public long? LibraryGameId { get; set; }
}

public sealed class LibraryTag
{
    public string Tag { get; set; } = "";
    public long Count { get; set; }
}

public sealed class CatalogTag
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class SettingsView
{
    public string DataDir { get; set; } = "";
    public string? F95Username { get; set; }
    public bool F95PasswordSet { get; set; }
    public bool F95CookiesSet { get; set; }
    public bool F95Authenticated { get; set; }
    public bool AppPasswordSet { get; set; }
    public ulong MaxAttachmentBytes { get; set; }
    public string TagClickAction { get; set; } = "library";
    public bool SaveSyncEnabled { get; set; } = true;
    public int SaveSyncMaxPerGame { get; set; } = 10;
    public bool SaveSyncRolling { get; set; } = true;
    public string SaveSyncNameFormat { get; set; } = "auto_{timestamp}";
}

public sealed class AuthMe
{
    public bool Configured { get; set; }
    public bool Authenticated { get; set; }
}

public sealed class VersionCheckResult
{
    public long GameId { get; set; }
    public string? StoredVersion { get; set; }
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string? F95Url { get; set; }
}

public sealed class DownloadLink
{
    public string Url { get; set; } = "";
    public string Host { get; set; } = "unknown";
    public string? Label { get; set; }
    /// <summary>Section / pack heading above hoster links (Episode 5, v0.4 Full, …).</summary>
    public string? Title { get; set; }
}

public sealed class PlaySessionDto
{
    public Guid ClientSessionId { get; set; }
    public string StartedAt { get; set; } = "";
    public string EndedAt { get; set; } = "";
    public long DurationSecs { get; set; }
    public string? ClientId { get; set; }
}

public sealed class PlaytimeSummary
{
    public long TotalSeconds { get; set; }
    public List<PlaySessionDto> Sessions { get; set; } = [];
}

public sealed class HubError
{
    public string? Error { get; set; }
}

public sealed class TokenResponse
{
    public string Token { get; set; } = "";
}

public sealed class AddGameRequest
{
    [JsonPropertyName("input")]
    public string Input { get; set; } = "";
}

public sealed class UpdateSettingsRequest
{
    public string? AppPassword { get; set; }
    public bool? AppPasswordRemove { get; set; }
    public string? F95Username { get; set; }
    public string? F95Password { get; set; }
    public ulong? MaxAttachmentBytes { get; set; }
    public string? TagClickAction { get; set; }
    public bool? SaveSyncEnabled { get; set; }
    public int? SaveSyncMaxPerGame { get; set; }
    public bool? SaveSyncRolling { get; set; }
    public string? SaveSyncNameFormat { get; set; }
}

public sealed class F95AuthResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
}

public sealed class OkResult
{
    public bool Ok { get; set; }
}

public sealed class StorageStats
{
    public string DataDir { get; set; } = "";
    public long DataDirBytes { get; set; }
    public long DatabaseBytes { get; set; }
    public long MediaCacheBytes { get; set; }
    public long SavesBytes { get; set; }
    public long PatchesBytes { get; set; }
}

public sealed class F95CookiesExport
{
    public string Cookies { get; set; } = "";
    public bool Set { get; set; }
}

public sealed class F95LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class F95CookiesRequest
{
    public string Cookies { get; set; } = "";
}

public sealed class UpdateGameUserData
{
    public string? PlayStatus { get; set; }
    public double? UserRating { get; set; }
    public bool? ClearUserRating { get; set; }
    public string? UserNotes { get; set; }
    public string? Title { get; set; }
    public bool? ResetTitle { get; set; }
    public string? Description { get; set; }
}

public sealed class SetCoverRequest
{
    public int ScreenshotIndex { get; set; }
}

public sealed class PlaytimeBatchRequest
{
    public List<PlaySessionDto> Sessions { get; set; } = [];
}
