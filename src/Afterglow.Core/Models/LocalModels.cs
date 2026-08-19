namespace Afterglow.Core.Models;

public sealed class AppConnectionConfig
{
    public BackendMode Mode { get; set; } = BackendMode.Unconfigured;
    public string? RemoteApiBase { get; set; }
    public string? AuthToken { get; set; }
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed class UiPreferences
{
    public const string DefaultAccentHex = "#3D9CF0";

    public string AccentHex { get; set; } = DefaultAccentHex;
    public double GlassBlur { get; set; } = 24;
    public bool CompactDensity { get; set; }
    public string LibraryRoot { get; set; } = "";
    public int DownloadConcurrency { get; set; } = 2;
    public bool AutoExtract { get; set; } = true;
    /// <summary>False until the user confirms a Steam-style library folder.</summary>
    public bool LibrarySetupComplete { get; set; }
    /// <summary>Library grid card scale (0.75–2.0). Default 1.0.</summary>
    public double LibraryCardScale { get; set; } = 1.0;
    /// <summary>Library sort key (`title_asc`, `playtime_desc`, …).</summary>
    public string LibrarySort { get; set; } = "title_asc";
    /// <summary>Play-status filter; empty means any status.</summary>
    public string LibraryPlayStatus { get; set; } = "";
    /// <summary>`all` or `installed`.</summary>
    public string LibraryInstallFilter { get; set; } = "all";
    /// <summary>True = grid cards; false = list.</summary>
    public bool LibraryGridView { get; set; } = true;
    public bool LibraryHoverPreviewsEnabled { get; set; } = true;
    public bool BrowseHoverPreviewsEnabled { get; set; } = true;
    /// <summary>Hover slideshow interval in milliseconds (400–10000).</summary>
    public int HoverPreviewIntervalMs { get; set; } = 1800;
    /// <summary>Semver (no leading v) the user chose to ignore for update prompts.</summary>
    public string? IgnoredUpdateVersion { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }
    /// <summary>Launch Afterglow when Windows starts (default off).</summary>
    public bool StartWithWindows { get; set; }
}

public sealed class LocalInstall
{
    public long GameId { get; set; }
    public string InstallPath { get; set; } = "";
    public string? ExePath { get; set; }
    public string? InstalledVersion { get; set; }
    public DateTimeOffset? LastLaunchedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum DownloadJobStatus
{
    Queued,
    Resolving,
    Downloading,
    Extracting,
    Completed,
    Failed,
    Cancelled,
    /// <summary>Host requires captcha/timer; Afterglow Browser is capturing the download.</summary>
    OpenedInBrowser,
}

public sealed class DownloadJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long GameId { get; set; }
    /// <summary>Display title for Steam-like download list (not the raw URL).</summary>
    public string? GameTitle { get; set; }
    public string SourceUrl { get; set; } = "";
    public string Host { get; set; } = "unknown";
    public DownloadJobStatus Status { get; set; } = DownloadJobStatus.Queued;
    public double Progress { get; set; }
    public string? Error { get; set; }
    public string? OutputPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Transient UI fields — not always persisted.</summary>
    public long BytesReceived { get; set; }
    public long? TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
}

public sealed class PendingPlaySession
{
    public Guid ClientSessionId { get; set; } = Guid.NewGuid();
    public long GameId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndedAt { get; set; }
    public long DurationSecs { get; set; }
    public bool Synced { get; set; }
}
