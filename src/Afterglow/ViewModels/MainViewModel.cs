using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Afterglow.Core;
using Afterglow.Core.Models;
using Afterglow.Downloads;
using Afterglow.HubClient;
using Afterglow.HubSidecar;
using Afterglow.Launcher;
using Afterglow.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Afterglow.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly Uri[] NavBannerUris =
    [
        new("avares://Afterglow/Assets/AfterglowBanner1.png"),
        new("avares://Afterglow/Assets/AfterglowBanner2.png"),
        new("avares://Afterglow/Assets/AfterglowBanner3.png")
    ];

    private readonly AfterglowAppService _app;
    private readonly MediaCacheService _media;
    private readonly Bitmap[] _navBanners;

    public MainViewModel(AfterglowAppService app, MediaCacheService media, ToastService toasts)
    {
        _app = app;
        _media = media;
        Toasts = toasts;
        _navBanners = NavBannerUris.Select(LoadBanner).ToArray();
        FirstRun = new FirstRunViewModel(app, OnConfigured);
        LibrarySetup = new LibrarySetupViewModel(app, OnLibrarySetupDone);
        Library = new LibraryViewModel(app, media, toasts, OpenGameAsync);
        Browse = new BrowseViewModel(app, media, toasts, OpenGameAsync);
        Downloads = new DownloadsViewModel(app, media);
        Settings = new SettingsViewModel(app, toasts, OnConfigured, OnFactoryReset);
        Detail = new GameDetailViewModel(app, media, () => _ = NavigateAsync("library"), () => _ = NavigateAsync("downloads"), toasts, OnDetailTagClickAsync);
        CurrentPage = FirstRun;
        _app.Downloads.JobChanged += OnDownloadJobChanged;
        _app.Downloads.JobRemoved += (_, id) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _downloadJobs.Remove(id);
            _terminalToastShown.Remove(id);
            RefreshDownloadsNav();
        });
        _app.Downloads.FinishedJobsCleared += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var id in _downloadJobs.Where(kv =>
                         kv.Value.Status is DownloadJobStatus.Completed or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)
                     .Select(kv => kv.Key).ToList())
            {
                _downloadJobs.Remove(id);
                _terminalToastShown.Remove(id);
            }
            RefreshDownloadsNav();
        });
    }

    public ToastService Toasts { get; }

    public FirstRunViewModel FirstRun { get; }
    public LibrarySetupViewModel LibrarySetup { get; }
    public LibraryViewModel Library { get; }
    public BrowseViewModel Browse { get; }
    public DownloadsViewModel Downloads { get; }
    public SettingsViewModel Settings { get; }
    public GameDetailViewModel Detail { get; }

    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private string _statusMessage = "Starting…";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _showShell;
    [ObservableProperty] private bool _showLocalBanner;
    [ObservableProperty] private string _navTitle = "Afterglow";
    [ObservableProperty] private Bitmap? _navBanner;
    [ObservableProperty] private bool _downloadsActive;
    [ObservableProperty] private string _downloadsNavDetail = "";
    [ObservableProperty] private double _downloadsNavProgress;
    [ObservableProperty] private bool _downloadsNavShowBar;

    private readonly Dictionary<Guid, DownloadJob> _downloadJobs = new();
    private readonly HashSet<Guid> _terminalToastShown = new();

    private void OnDownloadJobChanged(object? sender, DownloadJob job)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _downloadJobs[job.Id] = CloneJob(job);
            RefreshDownloadsNav();

            // Toasts only for significant outcomes — progress lives under the Downloads nav.
            if (!_terminalToastShown.Add(job.Id)) return;
            var host = string.IsNullOrWhiteSpace(job.Host) ? "download" : job.Host;
            var title = string.IsNullOrWhiteSpace(job.GameTitle) ? $"Game #{job.GameId}" : job.GameTitle!;
            switch (job.Status)
            {
                case DownloadJobStatus.Completed:
                    _ = ShowDownloadCompleteToastAsync(job.GameId, title, host);
                    break;
                case DownloadJobStatus.Failed:
                    Toasts.Error(job.Error ?? $"Download failed · {host}");
                    break;
                default:
                    _terminalToastShown.Remove(job.Id);
                    break;
            }
        });
    }

    private async Task ShowDownloadCompleteToastAsync(long gameId, string title, string host)
    {
        Bitmap? cover = null;
        try
        {
            var detail = await _app.Hub.GetGameAsync(gameId);
            var url = detail.CoverUrl ?? detail.CoverFullUrl;
            if (!string.IsNullOrWhiteSpace(url))
                cover = await _media.GetAsync(url);
        }
        catch { /* cover optional */ }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Toasts.DownloadComplete(title, $"Download complete · {host}", cover));
    }

    private void RefreshDownloadsNav()
    {
        static bool IsActive(DownloadJobStatus s) => s is
            DownloadJobStatus.Queued or DownloadJobStatus.Resolving or DownloadJobStatus.Downloading
            or DownloadJobStatus.Extracting or DownloadJobStatus.OpenedInBrowser;

        var active = _downloadJobs.Values.Where(j => IsActive(j.Status)).ToList();
        DownloadsActive = active.Count > 0;
        if (!DownloadsActive)
        {
            DownloadsNavDetail = "";
            DownloadsNavProgress = 0;
            DownloadsNavShowBar = false;
            return;
        }

        var inBrowser = active.Count(j => j.Status == DownloadJobStatus.OpenedInBrowser);
        var transferring = active.Where(j => j.Status is DownloadJobStatus.Downloading or DownloadJobStatus.Extracting or DownloadJobStatus.Resolving).ToList();
        if (transferring.Count > 0)
        {
            var avg = transferring.Average(j => j.Progress);
            DownloadsNavProgress = Math.Clamp(avg, 0, 1);
            DownloadsNavShowBar = true;
            DownloadsNavDetail = transferring.Count == 1
                ? $"{StatusLabel(transferring[0].Status)} · {avg:P0}"
                : $"{transferring.Count} active · {avg:P0}";
        }
        else if (inBrowser > 0)
        {
            DownloadsNavProgress = 0;
            DownloadsNavShowBar = false;
            DownloadsNavDetail = inBrowser == 1 ? "Waiting in browser" : $"{inBrowser} in browser";
        }
        else
        {
            DownloadsNavProgress = 0;
            DownloadsNavShowBar = false;
            DownloadsNavDetail = active.Count == 1 ? "Queued" : $"{active.Count} queued";
        }
    }

    private static string StatusLabel(DownloadJobStatus status) => status switch
    {
        DownloadJobStatus.Resolving => "Resolving",
        DownloadJobStatus.Downloading => "Downloading",
        DownloadJobStatus.Extracting => "Extracting",
        _ => status.ToString()
    };

    private static DownloadJob CloneJob(DownloadJob job) => new()
    {
        Id = job.Id,
        GameId = job.GameId,
        GameTitle = job.GameTitle,
        SourceUrl = job.SourceUrl,
        Host = job.Host,
        Status = job.Status,
        Progress = job.Progress,
        Error = job.Error,
        OutputPath = job.OutputPath,
        CreatedAt = job.CreatedAt,
        BytesReceived = job.BytesReceived,
        TotalBytes = job.TotalBytes,
        BytesPerSecond = job.BytesPerSecond
    };

    public async Task BootstrapAsync()
    {
        try
        {
            await _app.InitializeAsync();
            ThemeAccent.Apply(_app.Preferences.AccentHex);
            ShowLocalBanner = _app.IsLocalMode;
            if (_app.IsConfigured)
            {
                ShowShell = true;
                StatusMessage = _app.IsLocalMode ? "Local hub ready" : "Remote hub connected";
                await EnterConfiguredShellAsync();
            }
            else
            {
                ShowShell = false;
                CurrentPage = FirstRun;
                StatusMessage = "Choose Remote or Local hub";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "Startup failed";
            ShowShell = false;
            CurrentPage = FirstRun;
        }
    }

    private async void OnConfigured()
    {
        ShowShell = true;
        ShowLocalBanner = _app.IsLocalMode;
        ErrorMessage = null;
        StatusMessage = _app.IsLocalMode
            ? "Local hub — data stays on this PC"
            : "Remote hub connected";
        ThemeAccent.Apply(_app.Preferences.AccentHex);
        await EnterConfiguredShellAsync();
    }

    private async void OnLibrarySetupDone()
    {
        StatusMessage = "Library folder ready";
        await NavigateAsync("library");
        _ = CheckForAppUpdatesAsync();
    }

    private async void OnFactoryReset()
    {
        ShowShell = false;
        ShowLocalBanner = false;
        ErrorMessage = null;
        StatusMessage = "Factory reset — choose Remote or Local hub";
        CurrentPage = FirstRun;
        NavTitle = "Afterglow";
        NavBanner = null;
        await Task.CompletedTask;
    }

    private async Task EnterConfiguredShellAsync()
    {
        EnsureNavBanner();
        if (!_app.Preferences.LibrarySetupComplete)
        {
            CurrentPage = LibrarySetup;
            NavTitle = "Library folder";
            LibrarySetup.ResetFromPrefs();
            return;
        }

        await SyncDownloadNavFromDbAsync();
        await NavigateAsync("library");
        _ = SafeFlushPlaytimeAsync();
        _ = CheckForAppUpdatesAsync();
    }

    private async Task CheckForAppUpdatesAsync()
    {
        try
        {
            await Task.Delay(1200);
            var checker = new GitHubReleaseUpdateChecker();
            var update = await checker.CheckAsync(ignoredVersion: _app.Preferences.IgnoredUpdateVersion);
            if (update is null) return;

            var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            var choice = await ConfirmDialog.ShowChoicesAsync(
                owner,
                "Update available",
                $"Afterglow {update.Version} is available (you have {AppVersionInfo.Current}).\n\nOpen the GitHub releases page to download the installer or portable zip?",
                primaryLabel: "Open releases",
                secondaryLabel: "Ignore this version",
                cancelLabel: "Not now");

            if (choice == ConfirmDialogResult.Primary)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(update.HtmlUrl) { UseShellExecute = true });
                }
                catch
                {
                    Toasts.Warning("Couldn't open the browser. Visit github.com/goonedoutgames/afterglow/releases");
                }
            }
            else if (choice == ConfirmDialogResult.Secondary)
            {
                await _app.SetIgnoredUpdateVersionAsync(update.Version);
                Toasts.Info($"Won't ask again about {update.Version}.");
            }
        }
        catch
        {
            // Offline / rate-limited — ignore quietly.
        }
    }

    private async Task SyncDownloadNavFromDbAsync()
    {
        try
        {
            foreach (var job in await _app.Database.GetDownloadJobsAsync())
            {
                _downloadJobs[job.Id] = CloneJob(job);
                if (job.Status is DownloadJobStatus.Completed or DownloadJobStatus.Failed or DownloadJobStatus.Cancelled)
                    _terminalToastShown.Add(job.Id);
            }
            RefreshDownloadsNav();
        }
        catch
        {
            // Nav badge is best-effort.
        }
    }

    [RelayCommand]
    private async Task Navigate(string page) => await NavigateAsync(page);

    private static Bitmap LoadBanner(Uri uri)
    {
        using var stream = AssetLoader.Open(uri);
        return new Bitmap(stream);
    }

    /// <summary>Pick a random sidebar banner once per app session (when the shell first appears).</summary>
    private void EnsureNavBanner()
    {
        if (NavBanner is not null || _navBanners.Length == 0) return;
        NavBanner = _navBanners[Random.Shared.Next(_navBanners.Length)];
    }

    public async Task NavigateAsync(string page)
    {
        ErrorMessage = null;
        switch (page.ToLowerInvariant())
        {
            case "library":
                if (!_app.Preferences.LibrarySetupComplete)
                {
                    CurrentPage = LibrarySetup;
                    NavTitle = "Library folder";
                    LibrarySetup.ResetFromPrefs();
                    break;
                }
                CurrentPage = Library;
                NavTitle = "Library";
                await Library.RefreshAsync();
                break;
            case "browse":
                CurrentPage = Browse;
                NavTitle = "Browse";
                await Browse.EnsureLoadedAsync();
                break;
            case "downloads":
                CurrentPage = Downloads;
                NavTitle = "Downloads";
                await Downloads.RefreshAsync();
                break;
            case "settings":
                CurrentPage = Settings;
                NavTitle = "Settings";
                await Settings.LoadAsync();
                break;
        }
    }

    public async Task OpenGameAsync(long gameId)
    {
        CurrentPage = Detail;
        NavTitle = "Game";
        await Detail.LoadAsync(gameId);
    }

    private async Task OnDetailTagClickAsync(string tag)
    {
        var action = "library";
        try
        {
            var s = await _app.Hub.GetSettingsAsync();
            action = s.TagClickAction is "browse" ? "browse" : "library";
        }
        catch
        {
            action = Settings.TagClickAction is "browse" ? "browse" : "library";
        }

        if (action == "browse")
        {
            Browse.PrepareIncludeTag(tag);
            await NavigateAsync("browse");
        }
        else
        {
            Library.ApplyTagFilter(tag);
            await NavigateAsync("library");
        }
    }

    private async Task SafeFlushPlaytimeAsync()
    {
        try { await _app.PlaytimeSync.FlushAsync(); }
        catch { /* offline / missing endpoint */ }
    }
}

public partial class FirstRunViewModel : ViewModelBase
{
    private readonly AfterglowAppService _app;
    private readonly Action _onConfigured;

    public FirstRunViewModel(AfterglowAppService app, Action onConfigured)
    {
        _app = app;
        _onConfigured = onConfigured;
    }

    [ObservableProperty] private string _remoteUrl = "https://";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _busy;

    [RelayCommand]
    private async Task UseRemoteAsync()
    {
        Busy = true; Error = null; Status = "Connecting…";
        try
        {
            await _app.ConfigureRemoteAsync(RemoteUrl.Trim(), string.IsNullOrWhiteSpace(Password) ? null : Password);
            Status = "Connected.";
            _onConfigured();
        }
        catch (Exception ex) { Error = ex.Message; Status = null; }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task UseLocalAsync()
    {
        Busy = true; Error = null; Status = "Preparing local hub…";
        try
        {
            await _app.ConfigureLocalAsync(new Progress<string>(m => Status = m));
            Status = "Local hub ready.";
            _onConfigured();
        }
        catch (Exception ex)
        {
            Error = ex.Message + "\n\nAfterglow can auto-build from a sibling avn-hub repo (cargo), or place avn-hub.exe in the sidecar folder / set AFTERGLOW_AVN_HUB_PATH.";
            Status = null;
        }
        finally { Busy = false; }
    }
}

public partial class LibrarySetupViewModel : ViewModelBase
{
    private readonly AfterglowAppService _app;
    private readonly Action _onDone;

    public LibrarySetupViewModel(AfterglowAppService app, Action onDone)
    {
        _app = app;
        _onDone = onDone;
    }

    [ObservableProperty] private string _libraryRoot = AppPaths.DefaultLibraryRoot;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;

    public void ResetFromPrefs()
    {
        LibraryRoot = string.IsNullOrWhiteSpace(_app.Preferences.LibraryRoot)
            ? AppPaths.DefaultLibraryRoot
            : _app.Preferences.LibraryRoot;
        Error = null;
    }

    [RelayCommand]
    private void UseDefault() => LibraryRoot = AppPaths.DefaultLibraryRoot;

    public async Task PickFolderAsync(TopLevel topLevel)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Afterglow library folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            LibraryRoot = path;
    }

    [RelayCommand]
    private async Task ContinueAsync()
    {
        Busy = true; Error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(LibraryRoot))
                throw new InvalidOperationException("Choose a library folder.");
            Directory.CreateDirectory(LibraryRoot);
            var prefs = _app.Preferences;
            prefs.LibraryRoot = LibraryRoot.Trim();
            prefs.LibrarySetupComplete = true;
            await _app.SavePreferencesAsync(prefs);
            _onDone();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }
}

public partial class LibraryItemViewModel : ViewModelBase
{
    private readonly List<Bitmap> _galleryFrames = [];
    private DispatcherTimer? _hoverTimer;
    private int _hoverIndex;

    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public bool IsInstalled { get; init; }
    public long PlaytimeSeconds { get; init; }
    public string PlaytimeLabel { get; init; } = "";
    public string PlayStatusValue { get; init; } = "unplayed";
    public string PlayStatusLabel { get; init; } = "";
    public IBrush StatusBadgeBrush { get; init; } = PlayStatusPalette.Fill("unplayed");
    public IBrush StatusBadgeBorder { get; init; } = PlayStatusPalette.Border("unplayed");
    public string UserRatingLabel { get; init; } = "";
    public string F95RatingLabel { get; init; } = "";
    public double? UserRating { get; init; }
    public double? F95Rating { get; init; }
    public bool HasUserRating { get; init; }
    public bool HasF95Rating { get; init; }
    public string UserRatingText => HasUserRating ? $"{UserRating:0.0}" : "—";
    public string F95RatingText => HasF95Rating ? $"{F95Rating:0.0}" : "—";
    public IReadOnlyList<StarSlotViewModel> UserStars { get; init; } = [];
    public IReadOnlyList<StarSlotViewModel> F95Stars { get; init; } = [];
    public List<string> Tags { get; init; } = [];
    public string? CoverUrl { get; init; }
    public string? CoverVersion { get; init; }
    public List<string> ImageCandidates { get; init; } = [];
    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private AnimatedMedia? _coverAnimation;
    [ObservableProperty] private bool _hoverEnabled = true;
    [ObservableProperty] private int _hoverIntervalMs = 1800;
    public bool HoverGalleryReady { get; private set; }
    public bool HoverArmed { get; set; }

    public void SetCover(Bitmap? cover, AnimatedMedia? animatedCover = null)
    {
        CoverAnimation = animatedCover;
        Cover = cover ?? animatedCover?.Preview;
        if (cover is not null && (_galleryFrames.Count == 0 || !ReferenceEquals(_galleryFrames[0], cover)))
        {
            if (_galleryFrames.Count == 0) _galleryFrames.Add(cover);
            else _galleryFrames[0] = cover;
        }
    }

    public void SetHoverFrames(IEnumerable<Bitmap> extraFrames)
    {
        var cover = Cover ?? _galleryFrames.FirstOrDefault();
        _galleryFrames.Clear();
        if (cover is not null) _galleryFrames.Add(cover);
        foreach (var frame in extraFrames)
        {
            if (frame is null || ReferenceEquals(frame, cover)) continue;
            _galleryFrames.Add(frame);
        }
        HoverGalleryReady = true;
    }

    public void StartHoverPreview()
    {
        if (!HoverEnabled || !HoverArmed || _galleryFrames.Count <= 1 || CoverAnimation is not null) return;
        _hoverTimer ??= new DispatcherTimer();
        _hoverTimer.Tick -= OnHoverTick;
        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(HoverIntervalMs, 400, 10000));
        _hoverTimer.Start();
    }

    public void StopHoverPreview()
    {
        HoverArmed = false;
        _hoverTimer?.Stop();
        _hoverIndex = 0;
        if (_galleryFrames.Count > 0)
            Cover = _galleryFrames[0];
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        if (_galleryFrames.Count <= 1) return;
        _hoverIndex = (_hoverIndex + 1) % _galleryFrames.Count;
        Cover = _galleryFrames[_hoverIndex];
    }
}

public partial class LibraryTagFilterItem : ViewModelBase
{
    public required string Tag { get; init; }
    public long Count { get; init; }
    public string Label => Count > 0 ? $"{Tag} ({Count})" : Tag;
    [ObservableProperty] private bool _isSelected;
}

public sealed class LibraryChoice
{
    public LibraryChoice(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }
    public string Label { get; }
    public override string ToString() => Label;
}

public partial class CloudSaveItemViewModel : ViewModelBase
{
    public required GameSave Save { get; init; }
    public long Id => Save.Id;
    public string Filename => Save.Filename;
    public string UploadedLabel => DateFormats.FormatFriendly(Save.UploadedAt);
    public string SizeLabel => Save.Size > 0 ? FormatSize(Save.Size) : "";
    public string Meta => string.IsNullOrWhiteSpace(SizeLabel)
        ? UploadedLabel
        : $"{UploadedLabel} · {SizeLabel}";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.00} MB"
    };
}

public partial class CatalogItemViewModel : ViewModelBase
{
    public F95SearchResult Result { get; init; } = new();
    public string Title => Result.Title;
    public string Subtitle => string.Join(" · ", new[] { Result.Creator, Result.Version }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public string Meta
    {
        get
        {
            var parts = new List<string>();
            if (Result.Rating > 0) parts.Add($"★ {Result.Rating:0.0}");
            if (Result.Likes is long likes) parts.Add($"Likes {FormatCount(likes)}");
            if (Result.Views is long views) parts.Add($"Views {FormatCount(views)}");
            if (!string.IsNullOrWhiteSpace(Result.Date)) parts.Add(Result.Date);
            return string.Join(" · ", parts);
        }
    }
    public string? ThreadUrl => string.IsNullOrWhiteSpace(Result.Url) ? null : Result.Url;
    public List<string> Tags => Result.Tags;
    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private bool _isInLibrary;
    [ObservableProperty] private bool _isAdding;
    public string AddButtonLabel => IsInLibrary ? "Added" : IsAdding ? "Adding…" : "Add";
    public bool CanAdd => !IsInLibrary && !IsAdding;

    partial void OnIsInLibraryChanged(bool value)
    {
        OnPropertyChanged(nameof(AddButtonLabel));
        OnPropertyChanged(nameof(CanAdd));
    }

    partial void OnIsAddingChanged(bool value)
    {
        OnPropertyChanged(nameof(AddButtonLabel));
        OnPropertyChanged(nameof(CanAdd));
    }

    private static string FormatCount(long n)
    {
        if (n >= 1_000_000) return $"{n / 1_000_000.0:0.0}M";
        if (n >= 1_000) return n >= 10_000 ? $"{n / 1000}K" : $"{n / 1000.0:0.0}K";
        return n.ToString();
    }
}

public partial class CatalogPreviewShotViewModel : ViewModelBase
{
    public string Url { get; init; } = "";
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private bool _isSelected;
}

public partial class DownloadLinkItemViewModel : ViewModelBase
{
    public string Url { get; init; } = "";
    public string Host { get; init; } = "";
    public string? Platform { get; init; }
    public string? Title { get; init; }
    public string DisplayName { get; init; } = "";
    public bool IsMasked { get; init; }
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
    public string HostLabel => Host;
    public string HostInitial => string.IsNullOrWhiteSpace(Host) ? "?" : char.ToUpperInvariant(Host[0]).ToString();
    public string PlatformLabel => BrandIcons.PlatformLabel(Platform);
    public string ActionLabel => "Download";
    public string PlatformPath => BrandIcons.PlatformPath(Platform);
    public IBrush PlatformBrush => BrandIcons.PlatformBrush(Platform);
    [ObservableProperty] private Bitmap? _hostIcon;
}

/// <summary>One pack/extras group shown as a download tab.</summary>
public partial class DownloadPackTabViewModel : ViewModelBase
{
    public string Title { get; init; } = "";
    public ObservableCollection<DownloadLinkItemViewModel> Links { get; } = [];
}

public partial class LibraryViewModel : ViewModelBase
{
    private readonly AfterglowAppService _app;
    private readonly MediaCacheService _media;
    private readonly ToastService _toasts;
    private readonly Func<long, Task> _openGame;
    private readonly List<LibraryItemViewModel> _allGames = [];
    private bool _suppressFilterRefresh;
    private CancellationTokenSource? _coverCts;
    private const int TagCollapseLimit = 5;

    public LibraryViewModel(AfterglowAppService app, MediaCacheService media, ToastService toasts, Func<long, Task> openGame)
    {
        _app = app;
        _media = media;
        _toasts = toasts;
        _openGame = openGame;
        _suppressFilterRefresh = true;
        ApplySessionFromPrefs();
        ApplyCardSizeFromPrefs();
        _suppressFilterRefresh = false;
    }

    public ObservableCollection<LibraryItemViewModel> Games { get; } = [];
    public ObservableCollection<LibraryTagFilterItem> AvailableTags { get; } = [];
    public ObservableCollection<LibraryTagFilterItem> VisibleTags { get; } = [];
    public ObservableCollection<string> SelectedTags { get; } = [];

    public ObservableCollection<LibraryChoice> SortChoices { get; } =
    [
        new("title_asc", "Name (A–Z)"),
        new("title_desc", "Name (Z–A)"),
        new("updated_desc", "Recently updated"),
        new("rating_desc", "F95 rating"),
        new("user_rating_desc", "Your rating"),
        new("playtime_desc", "Playtime")
    ];

    public ObservableCollection<LibraryChoice> StatusChoices { get; } =
    [
        new("", "Any status"),
        new("unplayed", "Unplayed"),
        new("playing", "Playing"),
        new("completed", "Completed"),
        new("on_hold", "On hold"),
        new("dropped", "Dropped")
    ];

    public ObservableCollection<LibraryChoice> InstallChoices { get; } =
    [
        new("all", "All games"),
        new("installed", "Installed only")
    ];

    public ObservableCollection<LibraryChoice> CardSizeChoices { get; } =
    [
        new("small", "Small"),
        new("medium", "Medium"),
        new("large", "Large"),
        new("xl", "Extra Large"),
        new("xxxl", "XXXL")
    ];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private LibraryChoice? _sortBy;
    [ObservableProperty] private LibraryChoice? _statusFilter;
    [ObservableProperty] private LibraryChoice? _installFilter;
    [ObservableProperty] private LibraryChoice? _cardSizeChoice;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _mediaStatus;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _gridView = true;
    [ObservableProperty] private LibraryItemViewModel? _selectedGame;
    [ObservableProperty] private string _libraryCountLabel = "";
    [ObservableProperty] private double _cardWidth = 236;
    [ObservableProperty] private double _cardHeight = 455;
    [ObservableProperty] private double _metaFontSize = 13.5;
    [ObservableProperty] private bool _hasSelectedTags;
    [ObservableProperty] private string _selectedTagsLabel = "";
    [ObservableProperty] private bool _tagsExpanded;
    [ObservableProperty] private bool _showTagOverflow;
    [ObservableProperty] private string _tagOverflowLabel = "";

    public async Task RefreshAsync()
    {
        Busy = true; Error = null; MediaStatus = null;
        _media.ResetStats();
        _coverCts?.Cancel();
        _coverCts = new CancellationTokenSource();
        var coverCt = _coverCts.Token;
        try
        {
            var sortValue = SortBy?.Value ?? "title_asc";
            var statusValue = StatusFilter?.Value;
            if (string.IsNullOrWhiteSpace(statusValue)) statusValue = null;
            var tagsParam = SelectedTags.Count > 0 ? string.Join(",", SelectedTags) : null;

            var listTask = _app.Hub.GetLibraryAsync(
                string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
                statusValue,
                sortValue,
                tags: tagsParam);
            var tagsTask = _app.Hub.GetLibraryTagsAsync();
            await Task.WhenAll(listTask, tagsTask);
            var list = await listTask;
            var tagList = await tagsTask;

            RefreshAvailableTags(tagList);

            var installs = (await _app.Database.GetInstallsAsync()).ToDictionary(x => x.GameId);
            _allGames.Clear();
            foreach (var g in list)
            {
                installs.TryGetValue(g.Game.Id, out var install);
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(g.CoverUrl)) candidates.Add(g.CoverUrl);
                candidates.AddRange(g.PreviewUrls.Where(x => !string.IsNullOrWhiteSpace(x)));
                var playStatus = PlayStatusPalette.Normalize(g.Game.PlayStatus);
                var playStatusLabel = PlayStatusPalette.Label(playStatus);
                var userRating = g.Game.UserRating;
                var f95Rating = g.Game.Rating;
                var item = new LibraryItemViewModel
                {
                    Id = g.Game.Id,
                    Title = g.Game.Title,
                    Subtitle = string.Join(" · ", new[] { g.Game.Developer, g.Game.Version }.Where(x => !string.IsNullOrWhiteSpace(x))),
                    IsInstalled = install is not null,
                    PlaytimeSeconds = g.Game.PlaytimeSeconds,
                    PlaytimeLabel = LibraryPaths.FormatPlaytime(g.Game.PlaytimeSeconds),
                    PlayStatusValue = playStatus,
                    PlayStatusLabel = playStatusLabel,
                    StatusBadgeBrush = PlayStatusPalette.Fill(playStatus),
                    StatusBadgeBorder = PlayStatusPalette.Border(playStatus),
                    UserRating = userRating,
                    F95Rating = f95Rating,
                    HasUserRating = userRating is > 0,
                    UserRatingLabel = userRating is > 0 ? $"You ★ {userRating:0.0}" : "You —",
                    HasF95Rating = f95Rating is > 0,
                    F95RatingLabel = f95Rating is > 0 ? $"F95 ★ {f95Rating:0.0}" : "F95 —",
                    UserStars = BuildCompactStars(userRating),
                    F95Stars = BuildCompactStars(f95Rating),
                    Tags = TagHelpers.HumanTags(g.Game.Tags),
                    CoverUrl = g.CoverUrl,
                    CoverVersion = g.Game.UpdatedAt,
                    ImageCandidates = candidates
                };
                _allGames.Add(item);
            }

            if (sortValue is "playtime_desc")
            {
                var ordered = _allGames.OrderByDescending(g => g.PlaytimeSeconds).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase).ToList();
                _allGames.Clear();
                _allGames.AddRange(ordered);
            }

            ApplyLocalFilters();
            UpdateSelectedTagsLabel();
            var toLoad = _allGames.ToList();
            var missingUrls = toLoad.Count(g => g.ImageCandidates.Count == 0);
            Busy = false;
            _ = LoadCoversProgressivelyAsync(toLoad, missingUrls, coverCt);
        }
        catch (Exception ex) { Error = ex.Message; MediaStatus = null; Busy = false; }
    }

    private void RefreshAvailableTags(List<LibraryTag> tagList)
    {
        var selected = new HashSet<string>(SelectedTags, StringComparer.OrdinalIgnoreCase);
        AvailableTags.Clear();
        foreach (var t in tagList.Take(60))
        {
            if (string.IsNullOrWhiteSpace(t.Tag) || t.Tag.All(char.IsDigit)) continue;
            AvailableTags.Add(new LibraryTagFilterItem
            {
                Tag = t.Tag,
                Count = t.Count,
                IsSelected = selected.Contains(t.Tag)
            });
        }
        RebuildVisibleTags();
    }

    private void RebuildVisibleTags()
    {
        VisibleTags.Clear();
        if (TagsExpanded || AvailableTags.Count <= TagCollapseLimit)
        {
            foreach (var t in AvailableTags) VisibleTags.Add(t);
        }
        else
        {
            var selected = AvailableTags.Where(t => t.IsSelected).ToList();
            var rest = AvailableTags.Where(t => !t.IsSelected);
            foreach (var t in selected) VisibleTags.Add(t);
            foreach (var t in rest)
            {
                if (VisibleTags.Count >= TagCollapseLimit) break;
                VisibleTags.Add(t);
            }
        }

        var hidden = Math.Max(0, AvailableTags.Count - VisibleTags.Count);
        ShowTagOverflow = hidden > 0 || (TagsExpanded && AvailableTags.Count > TagCollapseLimit);
        TagOverflowLabel = TagsExpanded && AvailableTags.Count > TagCollapseLimit
            ? "Show less"
            : hidden > 0 ? $"+{hidden} more" : "";
    }

    [RelayCommand]
    private void ToggleTagOverflow()
    {
        TagsExpanded = !TagsExpanded;
        RebuildVisibleTags();
    }

    public void ApplyTagFilter(string tag)
    {
        var clean = tag.Trim();
        if (string.IsNullOrEmpty(clean) || clean.All(char.IsDigit)) return;
        SelectedTags.Clear();
        SelectedTags.Add(clean);
        HasSelectedTags = true;
        UpdateSelectedTagsLabel();
        foreach (var item in AvailableTags)
            item.IsSelected = string.Equals(item.Tag, clean, StringComparison.OrdinalIgnoreCase);
        RebuildVisibleTags();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ToggleTagFilter(LibraryTagFilterItem? item)
    {
        if (item is null) return;
        if (item.IsSelected)
        {
            item.IsSelected = false;
            var existing = SelectedTags.FirstOrDefault(t => string.Equals(t, item.Tag, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) SelectedTags.Remove(existing);
        }
        else
        {
            item.IsSelected = true;
            if (!SelectedTags.Any(t => string.Equals(t, item.Tag, StringComparison.OrdinalIgnoreCase)))
                SelectedTags.Add(item.Tag);
        }
        HasSelectedTags = SelectedTags.Count > 0;
        UpdateSelectedTagsLabel();
        RebuildVisibleTags();
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void ClearTagFilters()
    {
        SelectedTags.Clear();
        foreach (var t in AvailableTags) t.IsSelected = false;
        HasSelectedTags = false;
        UpdateSelectedTagsLabel();
        RebuildVisibleTags();
        _ = RefreshAsync();
    }

    private void UpdateSelectedTagsLabel()
    {
        SelectedTagsLabel = SelectedTags.Count == 0
            ? ""
            : "Filtered · " + string.Join(", ", SelectedTags);
    }

    partial void OnSortByChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || value is null) return;
        _ = PersistSessionAsync();
        _ = RefreshAsync();
    }

    partial void OnStatusFilterChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || value is null) return;
        _ = PersistSessionAsync();
        _ = RefreshAsync();
    }

    partial void OnInstallFilterChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || value is null) return;
        _ = PersistSessionAsync();
        ApplyLocalFilters();
    }

    partial void OnGridViewChanged(bool value)
    {
        if (_suppressFilterRefresh) return;
        _ = PersistSessionAsync();
    }

    partial void OnCardSizeChoiceChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || value is null) return;
        _ = PersistCardSizeAsync();
    }

    private void ApplySessionFromPrefs()
    {
        var prefs = _app.Preferences;
        SortBy = SortChoices.FirstOrDefault(c => string.Equals(c.Value, prefs.LibrarySort, StringComparison.OrdinalIgnoreCase))
                 ?? SortChoices[0];
        StatusFilter = StatusChoices.FirstOrDefault(c => string.Equals(c.Value, prefs.LibraryPlayStatus, StringComparison.OrdinalIgnoreCase))
                       ?? StatusChoices[0];
        InstallFilter = InstallChoices.FirstOrDefault(c => string.Equals(c.Value, prefs.LibraryInstallFilter, StringComparison.OrdinalIgnoreCase))
                        ?? InstallChoices[0];
        GridView = prefs.LibraryGridView;
    }

    private async Task PersistSessionAsync()
    {
        try
        {
            await _app.SaveLibrarySessionPrefsAsync(
                SortBy?.Value ?? "title_asc",
                StatusFilter?.Value ?? "",
                InstallFilter?.Value ?? "all",
                GridView);
        }
        catch { /* non-fatal */ }
    }

    private void ApplyCardSizeFromPrefs()
    {
        var scale = Math.Clamp(_app.Preferences.LibraryCardScale, 0.75, 2.0);
        var choice = scale switch
        {
            <= 0.85 => CardSizeChoices[0],
            <= 1.1 => CardSizeChoices[1],
            <= 1.35 => CardSizeChoices[2],
            <= 1.65 => CardSizeChoices[3],
            _ => CardSizeChoices[4]
        };
        CardSizeChoice = choice;
        ApplyCardMetrics(scale);
    }

    private async Task PersistCardSizeAsync()
    {
        var scale = CardSizeChoice?.Value switch
        {
            "small" => 0.8,
            "large" => 1.25,
            "xl" => 1.5,
            "xxxl" => 1.85,
            _ => 1.0
        };
        ApplyCardMetrics(scale);

        // Only touch card scale — never rewrite the whole prefs object (startup races
        // used to wipe LibrarySetupComplete / LibraryRoot and force the setup screen).
        try { await _app.SaveLibraryCardScaleAsync(scale); }
        catch { /* non-fatal */ }
    }

    private void ApplyCardMetrics(double scale)
    {
        // Wider base so covers read better; meta text scales up with size.
        CardWidth = Math.Round(236 * scale);
        CardHeight = Math.Round(455 * scale);
        MetaFontSize = Math.Clamp(13.5 * Math.Sqrt(scale), 12.5, 18);
    }

    private void ApplyLocalFilters()
    {
        var installedOnly = string.Equals(InstallFilter?.Value, "installed", StringComparison.OrdinalIgnoreCase);
        Games.Clear();
        foreach (var g in _allGames)
        {
            if (installedOnly && !g.IsInstalled) continue;
            Games.Add(g);
        }

        var installed = _allGames.Count(g => g.IsInstalled);
        LibraryCountLabel = installedOnly
            ? $"{Games.Count} installed"
            : $"{Games.Count} games · {installed} installed";
    }

    private static IReadOnlyList<StarSlotViewModel> BuildCompactStars(double? rating)
    {
        var value = rating is > 0 ? rating.Value : 0;
        var slots = new StarSlotViewModel[5];
        for (var i = 0; i < 5; i++)
        {
                slots[i] = new StarSlotViewModel
            {
                Index = i,
                StarSize = 15,
                Fill = Math.Clamp(value - i, 0, 1)
            };
        }
        return slots;
    }

    private async Task LoadCoversProgressivelyAsync(List<LibraryItemViewModel> items, int missingUrls, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(items.Select(item => LoadCoverAsync(item, cancellationToken)));
            if (cancellationToken.IsCancellationRequested) return;
            if (missingUrls > 0 || _media.LastError is not null)
            {
                MediaStatus = missingUrls > 0
                    ? $"{missingUrls} games have no cover from the hub." + (_media.LastError is null ? "" : $" {_media.LastError}")
                    : _media.LastError;
            }
        }
        catch (OperationCanceledException) { /* refresh superseded */ }
        catch (Exception ex) { MediaStatus = ex.Message; }
    }

    private async Task LoadCoverAsync(LibraryItemViewModel item, CancellationToken cancellationToken = default)
    {
        var hoverOn = _app.Preferences.LibraryHoverPreviewsEnabled;
        var request = MediaCacheRequest.Thumbnail(item.CoverVersion);
        Bitmap? cover = null;

        if (!string.IsNullOrWhiteSpace(item.CoverUrl))
            cover = await _media.GetAsync(item.CoverUrl, request, cancellationToken);

        if (cover is null)
        {
            foreach (var url in item.ImageCandidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(1))
            {
                cover = await _media.GetAsync(url, request, cancellationToken);
                if (cover is not null) break;
            }
        }

        if (cancellationToken.IsCancellationRequested) return;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.HoverEnabled = hoverOn;
            item.HoverIntervalMs = Math.Clamp(_app.Preferences.HoverPreviewIntervalMs, 400, 10000);
            item.SetCover(cover);
        });
    }

    public async Task BeginHoverAsync(LibraryItemViewModel item)
    {
        if (!item.HoverEnabled) return;
        item.HoverArmed = true;
        if (!item.HoverGalleryReady)
            await LoadHoverGalleryAsync(item);
        if (item.HoverArmed)
            item.StartHoverPreview();
    }

    private async Task LoadHoverGalleryAsync(LibraryItemViewModel item)
    {
        var extras = new List<Bitmap>();
        var request = MediaCacheRequest.Thumbnail(item.CoverVersion);
        foreach (var url in item.ImageCandidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(6))
        {
            if (!string.IsNullOrWhiteSpace(item.CoverUrl)
                && string.Equals(url, item.CoverUrl, StringComparison.OrdinalIgnoreCase))
                continue;
            var bmp = await _media.GetAsync(url, request);
            if (bmp is not null) extras.Add(bmp);
        }

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => item.SetHoverFrames(extras));
    }

    [RelayCommand]
    private async Task SearchAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (Busy) return;
        Busy = true; Error = null; MediaStatus = "Checking library for updates…";
        _toasts.Info("Checking library for updates…");
        try
        {
            var results = await _app.Hub.CheckAllUpdatesAsync();
            await RefreshAsync();
            var updates = results.Count(r => r.UpdateAvailable);
            MediaStatus = updates > 0
                ? $"Update check finished — {updates} game{(updates == 1 ? "" : "s")} with a newer F95 version."
                : "Update check finished — library is up to date.";
            if (updates > 0) _toasts.Warning(MediaStatus);
            else _toasts.Success(MediaStatus);
        }
        catch (Exception ex)
        {
            var msg = HubApiException.FriendlyMessage(ex, "Couldn't check for updates");
            Error = msg;
            MediaStatus = null;
            _toasts.Error(msg);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void ToggleView() => GridView = !GridView;

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedGame is null) return;
        await _openGame(SelectedGame.Id);
    }

    [RelayCommand]
    private async Task OpenGameAsync(LibraryItemViewModel? item)
    {
        if (item is null) return;
        await _openGame(item.Id);
    }
}

public partial class BrowseViewModel : ViewModelBase
{
    private readonly AfterglowAppService _app;
    private readonly MediaCacheService _media;
    private readonly ToastService _toasts;
    private readonly Func<long, Task> _openLibraryGame;
    private readonly HashSet<long> _libraryThreadIds = [];
    private readonly HashSet<long> _addingThreadIds = [];
    private readonly List<CatalogTag> _allCatalogTags = [];
    private bool _loaded;
    private bool _catalogLoaded;
    private bool _suppressFilterRefresh;
    private int _searchGeneration;

    public BrowseViewModel(AfterglowAppService app, MediaCacheService media, ToastService toasts, Func<long, Task> openLibraryGame)
    {
        _app = app;
        _media = media;
        _toasts = toasts;
        _openLibraryGame = openLibraryGame;
        _suppressFilterRefresh = true;
        SortBy = SortChoices[0];
        DatePreset = DateChoices[0];
        Engine = EngineChoices[0];
        StatusFilter = StatusChoices[0];
        SearchMode = SearchModeChoices[0];
        TagMode = TagModeOptions[0];
        for (var i = 0; i < 5; i++)
            PreviewF95Stars.Add(new StarSlotViewModel { Index = i, StarSize = 28 });
        _suppressFilterRefresh = false;
    }

    public ObservableCollection<CatalogItemViewModel> Results { get; } = [];
    public ObservableCollection<string> IncludeTags { get; } = [];
    public ObservableCollection<string> ExcludeTags { get; } = [];
    public ObservableCollection<CatalogTag> CatalogTagSuggestions { get; } = [];

    public ObservableCollection<LibraryChoice> SortChoices { get; } =
    [
        new("date", "Updated"),
        new("likes", "Likes"),
        new("views", "Views"),
        new("name", "Name"),
        new("rating", "Rating")
    ];

    public ObservableCollection<LibraryChoice> DateChoices { get; } =
    [
        new("0", "Any time"),
        new("7", "7 days"),
        new("30", "30 days"),
        new("90", "90 days"),
        new("365", "1 year")
    ];

    public ObservableCollection<LibraryChoice> EngineChoices { get; } =
    [
        new("", "Any engine"),
        new("Ren'Py", "Ren'Py"),
        new("Unity", "Unity"),
        new("HTML", "HTML"),
        new("RPGM", "RPGM"),
        new("VN", "VN"),
        new("Other", "Other")
    ];

    public ObservableCollection<LibraryChoice> StatusChoices { get; } =
    [
        new("", "Any status"),
        new("Completed", "Completed"),
        new("Abandoned", "Abandoned"),
        new("On Hold", "On Hold"),
        new("Cancelled", "Cancelled")
    ];

    public ObservableCollection<LibraryChoice> SearchModeChoices { get; } =
    [
        new("title", "Title"),
        new("creator", "Creator")
    ];

    public ObservableCollection<LibraryChoice> TagModeOptions { get; } =
    [
        new("and", "Match all (AND)"),
        new("or", "Match any (OR)")
    ];

    public ObservableCollection<CatalogPreviewShotViewModel> PreviewShots { get; } = [];
    public ObservableCollection<StarSlotViewModel> PreviewF95Stars { get; } = [];

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private LibraryChoice? _sortBy;
    [ObservableProperty] private LibraryChoice? _datePreset;
    [ObservableProperty] private LibraryChoice? _engine;
    [ObservableProperty] private LibraryChoice? _statusFilter;
    [ObservableProperty] private LibraryChoice? _searchMode;
    [ObservableProperty] private string _addByUrl = "";
    [ObservableProperty] private string _tagDraft = "";
    [ObservableProperty] private LibraryChoice? _tagMode;
    [ObservableProperty] private int _page = 1;
    [ObservableProperty] private int _pageRows = 90;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private bool _canGoPrev;
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private string _pageLabel = "Page 1";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _hasIncludeTags;
    [ObservableProperty] private bool _hasExcludeTags;
    [ObservableProperty] private bool _isPreviewOpen;
    [ObservableProperty] private bool _previewBusy;
    [ObservableProperty] private bool _isAddingGame;
    [ObservableProperty] private string? _previewError;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewSubtitle = "";
    [ObservableProperty] private string? _previewDescription;
    [ObservableProperty] private string _previewMeta = "";
    [ObservableProperty] private string _previewF95RatingText = "—";
    [ObservableProperty] private Bitmap? _previewCover;
    [ObservableProperty] private Bitmap? _previewSelectedShot;
    [ObservableProperty] private bool _previewInLibrary;
    [ObservableProperty] private bool _previewCanAdd;
    [ObservableProperty] private bool _previewCanOpenLibrary;
    [ObservableProperty] private bool _hasPreviewScreenshots;
    [ObservableProperty] private string _previewAddLabel = "Add to library";
    [ObservableProperty] private string? _previewThreadUrl;
    [ObservableProperty] private double _scrollOffsetY;
    [ObservableProperty] private List<string> _previewTagList = [];
    private long? _previewThreadId;
    private long? _previewLibraryGameId;
    private F95SearchResult? _previewResult;

    public bool CanSearch => !Busy && !IsAddingGame;

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanSearch));
    partial void OnIsAddingGameChanged(bool value) => OnPropertyChanged(nameof(CanSearch));

    public async Task EnsureLoadedAsync()
    {
        await EnsureCatalogTagsAsync();
        if (_loaded && Results.Count > 0) return;
        await ExecuteSearchAsync();
    }

    /// <summary>Set include tag and force the next EnsureLoadedAsync / search to apply it.</summary>
    public void PrepareIncludeTag(string tag)
    {
        var clean = ResolveCatalogTagName(tag.Trim());
        if (string.IsNullOrEmpty(clean) || clean.All(char.IsDigit)) return;
        IncludeTags.Clear();
        ExcludeTags.Clear();
        IncludeTags.Add(clean);
        HasIncludeTags = true;
        Page = 1;
        _loaded = false;
        RefreshCatalogSuggestions();
    }

    public void ApplyIncludeTag(string tag)
    {
        PrepareIncludeTag(tag);
        _ = SearchAsync();
    }

    [RelayCommand]
    private void AddIncludeTag()
    {
        TryAddIncludeTag(TagDraft);
        TagDraft = "";
    }

    [RelayCommand]
    private void ToggleCatalogTag(CatalogTag? tag)
    {
        if (tag is null || string.IsNullOrWhiteSpace(tag.Name)) return;
        var hit = IncludeTags.FirstOrDefault(x => string.Equals(x, tag.Name, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            IncludeTags.Remove(hit);
            HasIncludeTags = IncludeTags.Count > 0;
        }
        else
        {
            TryAddIncludeTag(tag.Name);
            return;
        }
        Page = 1;
        RefreshCatalogSuggestions();
        _ = SearchAsync();
    }

    private void TryAddIncludeTag(string? raw)
    {
        var t = ResolveCatalogTagName((raw ?? "").Trim());
        if (string.IsNullOrEmpty(t) || t.All(char.IsDigit) || IncludeTags.Count >= 10) return;
        if (IncludeTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) return;
        IncludeTags.Add(t);
        HasIncludeTags = true;
        Page = 1;
        RefreshCatalogSuggestions();
        _ = SearchAsync();
    }

    [RelayCommand]
    private void RemoveIncludeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var hit = IncludeTags.FirstOrDefault(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) IncludeTags.Remove(hit);
        HasIncludeTags = IncludeTags.Count > 0;
        Page = 1;
        RefreshCatalogSuggestions();
        _ = SearchAsync();
    }

    [RelayCommand]
    private void ClearIncludeTags()
    {
        IncludeTags.Clear();
        HasIncludeTags = false;
        Page = 1;
        RefreshCatalogSuggestions();
        _ = SearchAsync();
    }

    partial void OnTagDraftChanged(string value) => RefreshCatalogSuggestions();

    partial void OnSortByChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || !_loaded || value is null) return;
        Page = 1;
        _ = SearchAsync();
    }

    partial void OnDatePresetChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || !_loaded || value is null) return;
        Page = 1;
        _ = SearchAsync();
    }

    partial void OnEngineChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || !_loaded || value is null) return;
        Page = 1;
        _ = SearchAsync();
    }

    partial void OnStatusFilterChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || !_loaded || value is null) return;
        Page = 1;
        _ = SearchAsync();
    }

    partial void OnSearchModeChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || !_loaded || value is null) return;
        Page = 1;
        _ = SearchAsync();
    }

    partial void OnTagModeChanged(LibraryChoice? value)
    {
        if (_suppressFilterRefresh || !_loaded || value is null) return;
        Page = 1;
        _ = SearchAsync();
    }

    [RelayCommand]
    private void AddExcludeTag()
    {
        TryAddExcludeTag(ExcludeTagDraft);
        ExcludeTagDraft = "";
    }

    [RelayCommand]
    private void RemoveExcludeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        var hit = ExcludeTags.FirstOrDefault(x => string.Equals(x, tag, StringComparison.OrdinalIgnoreCase));
        if (hit is not null) ExcludeTags.Remove(hit);
        HasExcludeTags = ExcludeTags.Count > 0;
        Page = 1;
        _ = SearchAsync();
    }

    private void TryAddExcludeTag(string? raw)
    {
        var t = ResolveCatalogTagName((raw ?? "").Trim());
        if (string.IsNullOrEmpty(t) || t.All(char.IsDigit) || ExcludeTags.Count >= 10) return;
        if (ExcludeTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) return;
        if (IncludeTags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) return;
        ExcludeTags.Add(t);
        HasExcludeTags = true;
        Page = 1;
        _ = SearchAsync();
    }

    [ObservableProperty] private string _excludeTagDraft = "";

    private async Task EnsureCatalogTagsAsync()
    {
        if (_catalogLoaded) return;
        try
        {
            var list = await _app.Hub.GetCatalogTagsAsync(limit: 800);
            _allCatalogTags.Clear();
            _allCatalogTags.AddRange(list.Where(t => !string.IsNullOrWhiteSpace(t.Name)));
            _catalogLoaded = true;
            RefreshCatalogSuggestions();
        }
        catch
        {
            /* browse still works; free-typed tags may fail if unknown to hub */
        }
    }

    private void RefreshCatalogSuggestions()
    {
        CatalogTagSuggestions.Clear();
        var q = TagDraft.Trim();
        IEnumerable<CatalogTag> src = _allCatalogTags;
        if (!string.IsNullOrEmpty(q))
            src = src.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
        foreach (var t in src.Take(48))
            CatalogTagSuggestions.Add(t);
    }

    private string ResolveCatalogTagName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var hit = _allCatalogTags.FirstOrDefault(t =>
            string.Equals(t.Name, raw, StringComparison.OrdinalIgnoreCase));
        return hit?.Name ?? raw.Trim();
    }

    /// <summary>
    /// Prefer F95 numeric tag IDs when the catalog knows them — SAM ignores names.
    /// Falls back to names so the hub can still resolve via its tag map.
    /// </summary>
    private string? FormatTagsQuery(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0) return null;
        var parts = new List<string>(tags.Count);
        foreach (var tag in tags)
        {
            var hit = _allCatalogTags.FirstOrDefault(t =>
                string.Equals(t.Name, tag, StringComparison.OrdinalIgnoreCase));
            parts.Add(hit is not null ? hit.Id.ToString() : tag);
        }
        return string.Join(",", parts);
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        Page = 1;
        await ExecuteSearchAsync();
    }

    private async Task ExecuteSearchAsync()
    {
        if (IsAddingGame)
        {
            Status = "Wait for the current add to finish…";
            return;
        }

        var gen = ++_searchGeneration;
        Busy = true; Error = null; Status = null;
        try
        {
            await EnsureCatalogTagsAsync();
            if (gen != _searchGeneration) return;

            var dateDays = 0;
            if (DatePreset is not null && int.TryParse(DatePreset.Value, out var days))
                dateDays = days;

            var engineValue = Engine?.Value ?? "";
            var statusValue = StatusFilter?.Value ?? "";
            var mode = SearchMode?.Value ?? "title";
            var q = string.IsNullOrWhiteSpace(Query) ? null : Query.Trim();
            var prefixes = string.Join(",", new[]
            {
                string.IsNullOrWhiteSpace(engineValue) || engineValue == "Other" ? null : engineValue,
                string.IsNullOrWhiteSpace(statusValue) ? null : statusValue
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            Status = q is null
                ? "Loading F95 catalog…"
                : mode == "creator"
                    ? $"Searching creator “{q}”…"
                    : $"Searching “{q}”…";
            if (!string.IsNullOrWhiteSpace(q))
                _toasts.Info(Status);

            var pageResult = await _app.Hub.CatalogSearchAsync(
                query: mode == "title" ? q : null,
                creator: mode == "creator" ? q : null,
                page: Page,
                rows: PageRows,
                sort: SortBy?.Value ?? "date",
                dateDays: dateDays > 0 ? dateDays : null,
                tags: FormatTagsQuery(IncludeTags),
                notags: FormatTagsQuery(ExcludeTags),
                tagMode: TagMode?.Value ?? "and",
                prefixes: string.IsNullOrWhiteSpace(prefixes) ? null : prefixes);
            if (gen != _searchGeneration) return;

            var list = pageResult.Items ?? [];
            if (engineValue == "Other")
            {
                list = list.Where(r =>
                {
                    var prefixesLocal = (r.Prefixes ?? []).Select(p => p.ToLowerInvariant()).ToList();
                    string[] known = ["ren'py", "renpy", "unity", "html", "rpgm", "vn"];
                    return !prefixesLocal.Any(p => known.Contains(p) || p.Replace("'", "") == "renpy");
                }).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(engineValue))
            {
                var eng = engineValue.ToLowerInvariant();
                list = list.Where(r =>
                {
                    var prefixesLocal = (r.Prefixes ?? []).Select(p => p.ToLowerInvariant()).ToList();
                    if (prefixesLocal.Count == 0) return true;
                    return prefixesLocal.Any(p =>
                        p == eng || p.Replace("'", "") == eng.Replace("'", ""));
                }).ToList();
            }

            await RefreshLibraryThreadIdsAsync();
            if (gen != _searchGeneration) return;

            Results.Clear();
            foreach (var r in list)
            {
                var item = new CatalogItemViewModel
                {
                    Result = r,
                    IsInLibrary = r.InLibrary || _libraryThreadIds.Contains(r.ThreadId),
                    IsAdding = _addingThreadIds.Contains(r.ThreadId)
                };
                Results.Add(item);
                _ = LoadCoverAsync(item);
            }
            _loaded = true;
            if (pageResult.Page > 0) Page = pageResult.Page;
            TotalPages = pageResult.TotalPages;
            HasMore = pageResult.HasMore || (TotalPages > 0 && Page < TotalPages);
            CanGoPrev = Page > 1;
            CanGoNext = HasMore;
            PageLabel = TotalPages > 0 ? $"Page {Page} of {TotalPages}" : $"Page {Page}";
            var tagHint = IncludeTags.Count > 0 ? $" · tags: {string.Join(", ", IncludeTags)}" : "";
            var sortHint = SortBy is null ? "" : $" · sort: {SortBy.Label}";
            Status = Results.Count == 0
                ? "No SAM hits — try a shorter title, drop the subtitle, or clear filters."
                : $"{PageLabel} · {Results.Count}/{pageResult.Rows}{tagHint}{sortHint}"
                  + (HasMore ? " · more pages available" : " · last page");
            if (Results.Count == 0 && !string.IsNullOrWhiteSpace(q))
                _toasts.Warning(Status);
        }
        catch (Exception ex)
        {
            if (gen == _searchGeneration)
            {
                var msg = HubApiException.FriendlyMessage(ex, "Search failed");
                Error = msg;
                Status = null;
                _toasts.Error(msg);
            }
        }
        finally { if (gen == _searchGeneration) Busy = false; }
    }

    private void SyncCatalogCards(long threadId, bool? inLibrary = null, bool? isAdding = null)
    {
        foreach (var r in Results.Where(x => x.Result.ThreadId == threadId))
        {
            if (inLibrary is bool lib) r.IsInLibrary = lib;
            if (isAdding is bool add) r.IsAdding = add;
        }
    }

    private void ApplyPreviewRating(double rating)
    {
        PreviewF95RatingText = rating > 0 ? rating.ToString("0.0") : "—";
        for (var i = 0; i < PreviewF95Stars.Count; i++)
            PreviewF95Stars[i].Fill = Math.Clamp(rating - i, 0, 1);
    }

    private void ApplyPreviewTags(IEnumerable<string>? tags)
    {
        PreviewTagList = (tags ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
    }

    private async Task RefreshLibraryThreadIdsAsync()
    {
        _libraryThreadIds.Clear();
        try
        {
            foreach (var g in await _app.Hub.GetLibraryAsync())
            {
                if (g.Game.F95ThreadId is long tid)
                    _libraryThreadIds.Add(tid);
            }
        }
        catch { /* browse still works without library overlay */ }
    }

    private async Task LoadCoverAsync(CatalogItemViewModel item)
    {
        // Cover only — browsing used to prefetch up to 8 gallery frames per card for hover,
        // which made catalog searches feel very slow.
        var url = !string.IsNullOrWhiteSpace(item.Result.Cover)
            ? item.Result.Cover
            : item.Result.Screenshots.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(url)) return;
        var bmp = await _media.GetAsync(url);
        if (bmp is null) return;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => item.Cover = bmp);
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanGoNext) return;
        Page++;
        await ExecuteSearchAsync();
    }

    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (!CanGoPrev) return;
        Page--;
        await ExecuteSearchAsync();
    }

    [RelayCommand]
    private void OpenThread(CatalogItemViewModel? item)
    {
        if (item?.ThreadUrl is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.ThreadUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task OpenPreviewAsync(CatalogItemViewModel? item)
    {
        if (item is null) return;
        _previewThreadId = item.Result.ThreadId;
        _previewResult = item.Result;
        PreviewTitle = item.Result.Title;
        PreviewSubtitle = item.Subtitle;
        PreviewMeta = item.Meta;
        PreviewDescription = null;
        PreviewError = null;
        PreviewCover = item.Cover;
        PreviewSelectedShot = item.Cover;
        PreviewInLibrary = item.IsInLibrary;
        PreviewCanAdd = !item.IsInLibrary && !item.IsAdding;
        PreviewCanOpenLibrary = false;
        PreviewAddLabel = item.IsInLibrary ? "In library" : item.IsAdding ? "Adding…" : "Add to library";
        PreviewThreadUrl = item.ThreadUrl;
        ApplyPreviewRating(item.Result.Rating);
        ApplyPreviewTags(item.Result.Tags);
        PreviewShots.Clear();
        HasPreviewScreenshots = false;
        IsPreviewOpen = true;
        PreviewBusy = true;
        Status = $"Loading details · {item.Result.Title}";

        // Seed gallery from SAM list screenshots while thread scrape loads.
        foreach (var url in new[] { item.Result.Cover }.Concat(item.Result.Screenshots ?? [])
                     .Where(u => !string.IsNullOrWhiteSpace(u))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(24))
        {
            var shot = new CatalogPreviewShotViewModel { Url = url };
            PreviewShots.Add(shot);
            _ = LoadPreviewShotAsync(shot);
        }
        HasPreviewScreenshots = PreviewShots.Count > 0;

        try
        {
            var detail = await _app.Hub.CatalogPreviewAsync(
                item.Result.ThreadId.ToString(),
                item.Result.Title);
            if (_previewThreadId != detail.ThreadId) return;
            _previewResult = detail;
            PreviewTitle = detail.Title;
            PreviewSubtitle = string.Join(" · ", new[] { detail.Creator, detail.Version }.Where(x => !string.IsNullOrWhiteSpace(x)));
            PreviewDescription = string.IsNullOrWhiteSpace(detail.Description) ? null : detail.Description.Trim();
            PreviewInLibrary = detail.InLibrary || _libraryThreadIds.Contains(detail.ThreadId);
            _previewLibraryGameId = detail.LibraryGameId;
            PreviewCanOpenLibrary = PreviewInLibrary && _previewLibraryGameId is > 0;
            PreviewCanAdd = !PreviewInLibrary && !_addingThreadIds.Contains(detail.ThreadId);
            PreviewAddLabel = PreviewInLibrary
                ? "In library"
                : _addingThreadIds.Contains(detail.ThreadId) ? "Adding…" : "Add to library";
            PreviewThreadUrl = detail.Url;
            ApplyPreviewRating(detail.Rating);
            ApplyPreviewTags(detail.Tags);
            var parts = new List<string>();
            if (detail.Rating > 0) parts.Add($"★ {detail.Rating:0.0}");
            if (detail.Likes is long likes) parts.Add($"Likes {likes}");
            if (detail.Views is long views) parts.Add($"Views {views}");
            if (!string.IsNullOrWhiteSpace(detail.Date)) parts.Add(detail.Date);
            if (detail.Prefixes is { Count: > 0 })
                parts.Add(string.Join(" · ", detail.Prefixes.Take(4)));
            PreviewMeta = string.Join(" · ", parts);

            var urls = new[] { detail.Cover }.Concat(detail.Screenshots ?? [])
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList();
            PreviewShots.Clear();
            foreach (var url in urls)
            {
                var shot = new CatalogPreviewShotViewModel { Url = url };
                PreviewShots.Add(shot);
                _ = LoadPreviewShotAsync(shot);
            }
            HasPreviewScreenshots = PreviewShots.Count > 0;
            Status = $"Details · {detail.Title}";
        }
        catch (Exception ex)
        {
            var msg = HubApiException.FriendlyMessage(ex, "Couldn't load details");
            PreviewError = msg;
            _toasts.Error(msg);
        }
        finally
        {
            PreviewBusy = false;
        }
    }

    private async Task LoadPreviewShotAsync(CatalogPreviewShotViewModel shot)
    {
        var bmp = await _media.GetAsync(shot.Url);
        if (bmp is null) return;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            shot.Image = bmp;
            if (PreviewCover is null) PreviewCover = bmp;
            if (PreviewSelectedShot is null) PreviewSelectedShot = bmp;
        });
    }

    [RelayCommand]
    private void SelectPreviewShot(CatalogPreviewShotViewModel? shot)
    {
        if (shot?.Image is null) return;
        PreviewSelectedShot = shot.Image;
        foreach (var s in PreviewShots)
            s.IsSelected = ReferenceEquals(s, shot);
    }

    [RelayCommand]
    private void ClosePreview()
    {
        IsPreviewOpen = false;
        PreviewBusy = false;
        PreviewError = null;
        _previewThreadId = null;
        _previewResult = null;
        _previewLibraryGameId = null;
        PreviewCanOpenLibrary = false;
    }

    [RelayCommand]
    private void OpenPreviewThread()
    {
        if (string.IsNullOrWhiteSpace(PreviewThreadUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PreviewThreadUrl) { UseShellExecute = true });
        }
        catch (Exception ex) { PreviewError = ex.Message; }
    }

    [RelayCommand]
    private async Task OpenPreviewInLibraryAsync()
    {
        if (_previewLibraryGameId is not long id || id <= 0) return;
        IsPreviewOpen = false;
        await _openLibraryGame(id);
    }

    [RelayCommand]
    private async Task AddFromPreviewAsync()
    {
        if (!PreviewCanAdd || _previewThreadId is null || IsAddingGame) return;
        var tid = _previewThreadId.Value;
        var title = PreviewTitle;
        await RunAddAsync(tid.ToString(), title, PreviewCover, tid);
    }

    [RelayCommand]
    private async Task AddAsync(CatalogItemViewModel? item)
    {
        if (item is null || item.IsInLibrary || item.IsAdding || IsAddingGame) return;
        await RunAddAsync(item.Result.ThreadId.ToString(), item.Result.Title, item.Cover, item.Result.ThreadId);
    }

    [RelayCommand]
    private async Task AddByUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(AddByUrl) || Busy || IsAddingGame) return;
        var input = AddByUrl.Trim();
        await RunAddAsync(input, input, null, threadIdHint: null);
    }

    private async Task RunAddAsync(string input, string displayTitle, Bitmap? cover, long? threadIdHint)
    {
        IsAddingGame = true;
        Error = null;
        PreviewError = null;
        if (threadIdHint is long tid)
        {
            _addingThreadIds.Add(tid);
            SyncCatalogCards(tid, isAdding: true);
            if (_previewThreadId == tid)
            {
                PreviewCanAdd = false;
                PreviewAddLabel = "Adding…";
            }
        }
        else
        {
            PreviewCanAdd = false;
            PreviewAddLabel = "Adding…";
        }

        Status = $"Adding {displayTitle}… (this can take up to ~20s while the hub scrapes F95)";
        var sticky = _toasts.ShowSticky(
            "Talking to the hub / F95 — please wait…",
            title: $"Adding · {displayTitle}",
            cover: cover);

        try
        {
            // Browse cards already know the title; SAM often misses numeric-id search alone.
            var titleHint = !string.Equals(displayTitle, input, StringComparison.OrdinalIgnoreCase)
                            && displayTitle.Trim().Length >= 3
                            && !displayTitle.Contains("f95zone.to", StringComparison.OrdinalIgnoreCase)
                ? displayTitle
                : null;
            var detail = await _app.Hub.AddGameAsync(input, titleHint);
            var resolvedTid = detail.Game.F95ThreadId ?? threadIdHint;
            if (resolvedTid is long addedTid)
            {
                _libraryThreadIds.Add(addedTid);
                _addingThreadIds.Remove(addedTid);
                SyncCatalogCards(addedTid, inLibrary: true, isAdding: false);
                if (_previewThreadId == addedTid)
                {
                    _previewLibraryGameId = detail.Game.Id;
                    PreviewInLibrary = true;
                    PreviewCanOpenLibrary = true;
                    PreviewCanAdd = false;
                    PreviewAddLabel = "In library";
                }
            }

            if (threadIdHint is null)
                AddByUrl = "";

            Status = $"Added {detail.Game.Title}";
            _toasts.Dismiss(sticky);
            _toasts.ShowRich("Added to library", title: detail.Game.Title, cover: cover, kind: ToastKind.Success);
        }
        catch (Exception ex)
        {
            var msg = HubApiException.FriendlyMessage(ex, "Couldn't add this game");
            Error = msg;
            PreviewError = msg;
            Status = null;
            if (threadIdHint is long failTid)
            {
                _addingThreadIds.Remove(failTid);
                SyncCatalogCards(failTid, isAdding: false);
                if (_previewThreadId == failTid && !PreviewInLibrary)
                {
                    PreviewCanAdd = true;
                    PreviewAddLabel = "Add to library";
                }
            }
            else if (!PreviewInLibrary)
            {
                PreviewCanAdd = true;
                PreviewAddLabel = "Add to library";
            }
            _toasts.Dismiss(sticky);
            _toasts.Error(msg);
        }
        finally
        {
            IsAddingGame = _addingThreadIds.Count > 0;
        }
    }
}

public partial class DownloadsViewModel : ViewModelBase
{
    private readonly AfterglowAppService _app;
    private readonly MediaCacheService _media;
    private readonly Dictionary<Guid, DownloadItemViewModel> _byId = new();
    private readonly HashSet<long> _coverRequested = new();

    public DownloadsViewModel(AfterglowAppService app, MediaCacheService media)
    {
        _app = app;
        _media = media;
        _app.Downloads.JobChanged += (_, job) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Upsert(job));
        };
    }

    public ObservableCollection<DownloadItemViewModel> ActiveJobs { get; } = [];
    public ObservableCollection<DownloadItemViewModel> FinishedJobs { get; } = [];

    [ObservableProperty] private bool _hasActive;
    [ObservableProperty] private bool _hasFinished;
    [ObservableProperty] private bool _isEmpty = true;

    public async Task RefreshAsync()
    {
        ActiveJobs.Clear();
        FinishedJobs.Clear();
        _byId.Clear();
        _coverRequested.Clear();
        foreach (var j in await _app.Database.GetDownloadJobsAsync())
            Upsert(j);
        Recount();
    }

    [RelayCommand]
    private async Task Refresh() => await RefreshAsync();

    [RelayCommand]
    private async Task Cancel(DownloadItemViewModel? item)
    {
        if (item is null) return;
        await _app.Downloads.CancelJobAsync(item.Id);
    }

    [RelayCommand]
    private async Task Remove(DownloadItemViewModel? item)
    {
        if (item is null) return;
        // Active rows use Cancel; if Remove is invoked on an active job, stop it first.
        if (item.IsActive)
            await _app.Downloads.CancelJobAsync(item.Id);
        await _app.Downloads.RemoveJobAsync(item.Id);
        if (_byId.Remove(item.Id))
        {
            ActiveJobs.Remove(item);
            FinishedJobs.Remove(item);
            Recount();
        }
    }

    [RelayCommand]
    private async Task ClearFinished()
    {
        await _app.Downloads.ClearFinishedAsync();
        FinishedJobs.Clear();
        foreach (var id in _byId.Where(kv => !kv.Value.IsActive).Select(kv => kv.Key).ToList())
            _byId.Remove(id);
        Recount();
    }

    private void Upsert(DownloadJob job)
    {
        if (!_byId.TryGetValue(job.Id, out var vm))
        {
            vm = new DownloadItemViewModel();
            _byId[job.Id] = vm;
        }
        vm.Apply(job);
        EnsureCover(vm);

        var wantActive = vm.IsActive;
        var inActive = ActiveJobs.Contains(vm);
        var inFinished = FinishedJobs.Contains(vm);
        if (wantActive)
        {
            if (inFinished) FinishedJobs.Remove(vm);
            if (!inActive) ActiveJobs.Insert(0, vm);
        }
        else
        {
            if (inActive) ActiveJobs.Remove(vm);
            if (!inFinished) FinishedJobs.Insert(0, vm);
        }
        Recount();
    }

    private void EnsureCover(DownloadItemViewModel vm)
    {
        if (vm.Cover is not null || vm.GameId <= 0) return;
        if (!_coverRequested.Add(vm.GameId)) return;
        _ = LoadCoverAsync(vm.GameId);
    }

    private async Task LoadCoverAsync(long gameId)
    {
        try
        {
            var detail = await _app.Hub.GetGameAsync(gameId);
            var url = detail.CoverUrl ?? detail.CoverFullUrl;
            if (string.IsNullOrWhiteSpace(url)) return;
            var bmp = await _media.GetAsync(url);
            if (bmp is null) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in _byId.Values.Where(x => x.GameId == gameId))
                    item.Cover = bmp;
            });
        }
        catch
        {
            _coverRequested.Remove(gameId);
        }
    }

    private void Recount()
    {
        HasActive = ActiveJobs.Count > 0;
        HasFinished = FinishedJobs.Count > 0;
        IsEmpty = !HasActive && !HasFinished;
    }
}

public partial class DownloadItemViewModel : ViewModelBase
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private long _gameId;
    [ObservableProperty] private string _title = "Game";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private Bitmap? _cover;

    public void Apply(DownloadJob job)
    {
        Id = job.Id;
        GameId = job.GameId;
        Title = string.IsNullOrWhiteSpace(job.GameTitle) ? $"Game #{job.GameId}" : job.GameTitle!;
        Subtitle = job.Host switch
        {
            "archive" => "Local archive",
            "folder" => "Link folder",
            "local" => "Local file",
            _ => string.IsNullOrWhiteSpace(job.Host) ? "" : job.Host
        };
        Progress = job.Progress;
        Error = job.Status is DownloadJobStatus.Failed ? job.Error : null;
        IsActive = job.Status is DownloadJobStatus.Queued or DownloadJobStatus.Resolving
            or DownloadJobStatus.Downloading or DownloadJobStatus.Extracting
            or DownloadJobStatus.OpenedInBrowser;

        StatusText = job.Status switch
        {
            DownloadJobStatus.Queued => "Queued",
            DownloadJobStatus.Resolving => "Resolving link…",
            DownloadJobStatus.OpenedInBrowser => "Waiting in Afterglow Browser…",
            DownloadJobStatus.Downloading => FormatSpeed(job),
            DownloadJobStatus.Extracting when job.Host is "folder" => "Linking folder…",
            DownloadJobStatus.Extracting when job.Host is "archive" or "local" => "Extracting archive…",
            DownloadJobStatus.Extracting => "Extracting…",
            DownloadJobStatus.Completed when job.Host is "folder" => "Folder linked",
            DownloadJobStatus.Completed when job.Host is "archive" or "local" => "Installed",
            DownloadJobStatus.Completed => "Completed",
            DownloadJobStatus.Failed => "Failed",
            DownloadJobStatus.Cancelled => "Cancelled",
            _ => job.Status.ToString()
        };

        ProgressText = job.Status switch
        {
            DownloadJobStatus.Downloading =>
                job.TotalBytes is > 0
                    ? $"{FormatBytes(job.BytesReceived)} / {FormatBytes(job.TotalBytes.Value)} · {job.Progress:P0}"
                    : $"{FormatBytes(job.BytesReceived)} · {job.Progress:P0}",
            DownloadJobStatus.Extracting when job.Host is "archive" or "local" =>
                job.TotalBytes is > 0 && job.TotalBytes <= 100_000 && job.BytesReceived <= job.TotalBytes
                    ? $"{job.BytesReceived} / {job.TotalBytes} files · {job.Progress:P0}"
                    : job.TotalBytes is > 0
                        ? $"{FormatBytes(job.BytesReceived)} / {FormatBytes(job.TotalBytes.Value)} · {job.Progress:P0}"
                        : $"{job.Progress:P0}",
            DownloadJobStatus.Extracting =>
                job.Progress > 0 ? $"{job.Progress:P0}" : "",
            DownloadJobStatus.Completed => "100%",
            _ => job.Progress > 0 ? $"{job.Progress:P0}" : ""
        };
    }

    private static string FormatSpeed(DownloadJob job)
    {
        if (job.BytesPerSecond <= 0) return "Downloading…";
        return $"Downloading · {FormatBytes((long)job.BytesPerSecond)}/s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = Math.Max(0, bytes);
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }
}

public partial class SettingsViewModel : ViewModelBase
{
    private readonly AfterglowAppService _app;
    private readonly ToastService _toasts;
    private readonly Action _onReconfigured;
    private readonly Action _onFactoryReset;

    public SettingsViewModel(AfterglowAppService app, ToastService toasts, Action onReconfigured, Action onFactoryReset)
    {
        _app = app;
        _toasts = toasts;
        _onReconfigured = onReconfigured;
        _onFactoryReset = onFactoryReset;
    }

    [ObservableProperty] private string _modeLabel = "";
    [ObservableProperty] private string _remoteUrl = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _libraryRoot = "";
    [ObservableProperty] private string _accentHex = UiPreferences.DefaultAccentHex;
    [ObservableProperty] private Color _accentColor = Color.Parse(UiPreferences.DefaultAccentHex);
    [ObservableProperty] private bool _saveSyncEnabled = true;
    [ObservableProperty] private string _saveSyncMaxText = "10";
    [ObservableProperty] private bool _saveSyncRolling = true;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _sidecarInfo = "";
    [ObservableProperty] private string _f95Username = "";
    [ObservableProperty] private string _f95Password = "";
    [ObservableProperty] private string _f95Cookies = "";
    [ObservableProperty] private string _f95Status = "F95 status: unknown";
    [ObservableProperty] private string _hubAppPassword = "";
    [ObservableProperty] private string _appPasswordStatus = "App password: unknown";
    [ObservableProperty] private bool _appPasswordSet;
    [ObservableProperty] private string _tagClickAction = "library";
    [ObservableProperty] private string _storageSummary = "Storage: not loaded";
    [ObservableProperty] private bool _libraryHoverPreviewsEnabled = true;
    [ObservableProperty] private string _hoverPreviewIntervalText = "1800";
    [ObservableProperty] private bool _startWithWindows;
    public string[] TagClickOptions { get; } = ["library", "browse"];

    private void ReportOk(string message)
    {
        Error = null;
        Status = message;
        _toasts.Success(message);
    }

    private void ReportFail(string message)
    {
        Status = null;
        Error = message;
        _toasts.Error(message);
    }

    public async Task LoadAsync()
    {
        ModeLabel = _app.Connection.Mode.ToString();
        RemoteUrl = _app.Connection.RemoteApiBase ?? "";
        LibraryRoot = _app.Preferences.LibraryRoot;
        AccentHex = _app.Preferences.AccentHex;
        LibraryHoverPreviewsEnabled = _app.Preferences.LibraryHoverPreviewsEnabled;
        HoverPreviewIntervalText = Math.Clamp(_app.Preferences.HoverPreviewIntervalMs, 400, 10000).ToString();
        StartWithWindows = _app.Preferences.StartWithWindows;
        if (ThemeAccent.TryParseColor(AccentHex, out var c))
            AccentColor = c;
        var found = SidecarBootstrap.FindExistingExecutable();
        SidecarInfo = found is null
            ? "No local avn-hub.exe found yet — Use Local / Prepare sidecar will locate or build one."
            : $"Sidecar binary: {found}";
        try
        {
            var s = await _app.Hub.GetSettingsAsync();
            ApplyHubSettings(s);
            try
            {
                var st = await _app.Hub.GetStorageAsync();
                StorageSummary =
                    $"Data dir: {st.DataDir}\n" +
                    $"DB {FormatBytes(st.DatabaseBytes)} · media {FormatBytes(st.MediaCacheBytes)} · " +
                    $"saves {FormatBytes(st.SavesBytes)} · patches {FormatBytes(st.PatchesBytes)} · " +
                    $"total {FormatBytes(st.DataDirBytes)}";
            }
            catch (Exception ex)
            {
                StorageSummary = "Storage unavailable: " + ex.Message;
            }
        }
        catch (Exception ex) { ReportFail("Could not load hub settings: " + ex.Message); }
    }

    private void ApplyHubSettings(Afterglow.Core.Models.SettingsView s)
    {
        SaveSyncEnabled = s.SaveSyncEnabled;
        SaveSyncMaxText = s.SaveSyncMaxPerGame.ToString();
        SaveSyncRolling = s.SaveSyncRolling;
        F95Username = s.F95Username ?? "";
        F95Status = s.F95Authenticated
            ? $"Status: authenticated{(s.F95CookiesSet ? " · cookies saved" : "")}"
            : $"Status: not authenticated{(s.F95CookiesSet ? " · cookies saved" : "")} — login required for Browse/links.";
        AppPasswordSet = s.AppPasswordSet;
        AppPasswordStatus = s.AppPasswordSet
            ? "Password is configured. Clients authenticate with a Bearer token."
            : "No password set — API is open. Set one for production / Remote.";
        TagClickAction = s.TagClickAction is "browse" ? "browse" : "library";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {units[i]}";
    }

    partial void OnAccentColorChanged(Color value)
    {
        AccentHex = ThemeAccent.ToHex(value);
        ThemeAccent.Apply(AccentHex);
    }

    partial void OnAccentHexChanged(string value)
    {
        if (ThemeAccent.TryParseColor(value, out var c) && c != AccentColor)
            AccentColor = c;
    }

    [RelayCommand]
    private async Task F95LoginAsync()
    {
        Busy = true; Error = null; Status = "Logging into F95Zone…";
        try
        {
            var res = await _app.Hub.F95LoginAsync(F95Username.Trim(), F95Password);
            F95Password = "";
            var msg = string.IsNullOrWhiteSpace(res.Message) ? "Logged in to F95Zone." : res.Message;
            await LoadAsync();
            ReportOk(msg);
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task F95CookiesAsync()
    {
        Busy = true; Error = null; Status = "Saving F95 cookies…";
        try
        {
            var res = await _app.Hub.F95CookiesAsync(F95Cookies);
            F95Cookies = "";
            var msg = string.IsNullOrWhiteSpace(res.Message) ? "Cookies saved." : res.Message;
            await LoadAsync();
            ReportOk(msg);
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SetAppPasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(HubAppPassword))
        {
            ReportFail("Enter a new hub app password first.");
            return;
        }
        Busy = true; Error = null; Status = "Setting hub app password…";
        try
        {
            ApplyHubSettings(await _app.Hub.UpdateSettingsAsync(new UpdateSettingsRequest { AppPassword = HubAppPassword }));
            HubAppPassword = "";
            ReportOk("Hub app password updated.");
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RemoveAppPasswordAsync()
    {
        Busy = true; Error = null; Status = "Removing hub app password…";
        try
        {
            ApplyHubSettings(await _app.Hub.UpdateSettingsAsync(new UpdateSettingsRequest { AppPasswordRemove = true }));
            ReportOk("Hub app password removed — API is open until you set one again.");
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SaveTagClickAsync()
    {
        Busy = true; Error = null; Status = "Saving tag click action…";
        try
        {
            ApplyHubSettings(await _app.Hub.UpdateSettingsAsync(new UpdateSettingsRequest { TagClickAction = TagClickAction }));
            ReportOk(TagClickAction == "browse" ? "Tag clicks open Browse." : "Tag clicks filter Library.");
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task PurgeHubMediaAsync()
    {
        Busy = true; Error = null; Status = "Purging hub media cache…";
        try
        {
            await _app.Hub.PurgeMediaAsync();
            await LoadAsync();
            ReportOk("Hub media cache purged.");
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void ResetAccent()
    {
        AccentHex = UiPreferences.DefaultAccentHex;
        AccentColor = Color.Parse(UiPreferences.DefaultAccentHex);
        ThemeAccent.Apply(AccentHex);
    }

    public async Task PickLibraryFolderAsync(TopLevel topLevel)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose library folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            LibraryRoot = path;
    }

    [RelayCommand]
    private async Task SaveLocalPrefsAsync()
    {
        Busy = true; Error = null; Status = "Saving local preferences…";
        try
        {
            if (!ThemeAccent.TryParseColor(AccentHex, out _))
                throw new InvalidOperationException("Accent must be a valid hex color like #3D9CF0.");
            if (!int.TryParse(HoverPreviewIntervalText.Trim(), out var hoverMs))
                throw new InvalidOperationException("Hover interval must be a number (milliseconds).");
            hoverMs = Math.Clamp(hoverMs, 400, 10000);
            HoverPreviewIntervalText = hoverMs.ToString();

            var existing = _app.Preferences;
            await _app.SavePreferencesAsync(new UiPreferences
            {
                AccentHex = AccentHex.StartsWith('#') ? AccentHex : "#" + AccentHex,
                GlassBlur = existing.GlassBlur,
                CompactDensity = existing.CompactDensity,
                LibraryRoot = LibraryRoot,
                DownloadConcurrency = existing.DownloadConcurrency,
                AutoExtract = existing.AutoExtract,
                LibrarySetupComplete = true,
                LibraryCardScale = existing.LibraryCardScale,
                LibrarySort = existing.LibrarySort,
                LibraryPlayStatus = existing.LibraryPlayStatus,
                LibraryInstallFilter = existing.LibraryInstallFilter,
                LibraryGridView = existing.LibraryGridView,
                LibraryHoverPreviewsEnabled = LibraryHoverPreviewsEnabled,
                BrowseHoverPreviewsEnabled = false,
                HoverPreviewIntervalMs = hoverMs,
                IgnoredUpdateVersion = existing.IgnoredUpdateVersion,
                WindowWidth = existing.WindowWidth,
                WindowHeight = existing.WindowHeight,
                WindowX = existing.WindowX,
                WindowY = existing.WindowY,
                WindowMaximized = existing.WindowMaximized,
                StartWithWindows = StartWithWindows
            });
            WindowsStartup.SetEnabled(StartWithWindows);
            ThemeAccent.Apply(_app.Preferences.AccentHex);
            ReportOk("Local preferences saved.");
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SaveHubSyncAsync()
    {
        Busy = true; Error = null; Status = "Saving hub sync settings…";
        try
        {
            if (!int.TryParse(SaveSyncMaxText.Trim(), out var max) || max < 1)
                throw new InvalidOperationException("Max saves per game must be a positive number.");
            SaveSyncMaxText = max.ToString();
            await _app.Hub.UpdateSettingsAsync(new UpdateSettingsRequest
            {
                SaveSyncEnabled = SaveSyncEnabled,
                SaveSyncMaxPerGame = max,
                SaveSyncRolling = SaveSyncRolling
            });
            ReportOk("Save sync settings updated on hub.");
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SwitchRemoteAsync()
    {
        Busy = true; Error = null; Status = "Switching to Remote…";
        try
        {
            await _app.SwitchToRemoteAsync(RemoteUrl.Trim(), string.IsNullOrWhiteSpace(Password) ? null : Password);
            ModeLabel = "Remote";
            Password = "";
            ReportOk("Switched to Remote (sidecar stopped).");
            _onReconfigured();
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SwitchLocalAsync()
    {
        Busy = true; Error = null; Status = "Preparing Local hub…";
        try
        {
            await _app.SwitchToLocalAsync(new Progress<string>(m => Status = m));
            ModeLabel = "Local";
            SidecarInfo = _app.Sidecar.LastExecutable is null
                ? SidecarInfo
                : $"Sidecar binary: {_app.Sidecar.LastExecutable}";
            ReportOk("Switched to Local — data stays on this PC.");
            _onReconfigured();
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task PrepareSidecarAsync()
    {
        Busy = true; Error = null; Status = "Preparing sidecar…";
        try
        {
            var path = await SidecarBootstrap.EnsureAsync(new Progress<string>(m => Status = m), forceRebuild: true);
            SidecarInfo = $"Sidecar binary: {path}";
            ReportOk("Sidecar ready (not started until you Use Local).");
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task ResetFactoryAsync()
    {
        Busy = true; Error = null; Status = "Resetting…";
        try
        {
            await _app.ResetToFactoryAsync();
            ThemeAccent.Apply(UiPreferences.DefaultAccentHex);
            ReportOk("Factory reset complete.");
            _onFactoryReset();
        }
        catch (Exception ex) { ReportFail(ex.Message); }
        finally { Busy = false; }
    }
}

public partial class ScreenshotThumbViewModel : ViewModelBase
{
    public int Index { get; init; }
    public string Url { get; init; } = "";
    public string? FallbackUrl { get; init; }
    public bool CanSetCover { get; init; }
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private AnimatedMedia? _animation;
    [ObservableProperty] private bool _isSelected;
    public bool IsAnimated => Animation?.IsAnimated == true;
    partial void OnAnimationChanged(AnimatedMedia? value) => OnPropertyChanged(nameof(IsAnimated));
}

public partial class StarSlotViewModel : ViewModelBase
{
    public int Index { get; init; }
    public double StarSize { get; init; } = 28;
    [ObservableProperty] private double _fill;
    public double FillWidth => Math.Clamp(Fill, 0, 1) * StarSize;
    /// <summary>Invariant half-star command parameter (e.g. "1.5").</summary>
    public string HalfParam => (Index + 0.5).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Invariant full-star command parameter (e.g. "2.0").</summary>
    public string FullParam => (Index + 1.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    partial void OnFillChanged(double value) => OnPropertyChanged(nameof(FillWidth));
}

public partial class PlayStatusPillViewModel : ViewModelBase
{
    public required string Value { get; init; }
    public required string Label { get; init; }
    [ObservableProperty] private bool _isActive;

    public IBrush PillBrush => IsActive ? ActiveFill : SoftFill;
    public IBrush PillBorder => IsActive ? ActiveBorder : SoftBorder;
    public IBrush PillForeground => IsActive ? Brushes.White : SoftForeground;

    private IBrush SoftFill => new SolidColorBrush(Color.Parse("#22161A22"));
    private IBrush SoftBorder => new SolidColorBrush(Color.Parse("#44FFFFFF"));
    private IBrush SoftForeground => new SolidColorBrush(Color.Parse("#B0B8C4"));

    private IBrush ActiveFill => PlayStatusPalette.Fill(Value);
    private IBrush ActiveBorder => PlayStatusPalette.Border(Value);

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(PillBrush));
        OnPropertyChanged(nameof(PillBorder));
        OnPropertyChanged(nameof(PillForeground));
    }
}

public partial class GameDetailViewModel : ViewModelBase
{
    private readonly AfterglowAppService _app;
    private readonly MediaCacheService _media;
    private readonly ToastService _toasts;
    private readonly Action _back;
    private readonly Action _goDownloads;
    private readonly Func<string, Task> _onTagClick;

    public GameDetailViewModel(AfterglowAppService app, MediaCacheService media, Action back, Action goDownloads, ToastService toasts, Func<string, Task> onTagClick)
    {
        _app = app;
        _media = media;
        _back = back;
        _goDownloads = goDownloads;
        _toasts = toasts;
        _onTagClick = onTagClick;
        foreach (var (value, label) in new[]
                 {
                     ("unplayed", "Unplayed"), ("playing", "Playing"),
                     ("completed", "Completed"), ("dropped", "Dropped")
                 })
            StatusPills.Add(new PlayStatusPillViewModel { Value = value, Label = label });
        for (var i = 0; i < 5; i++)
        {
            YourStars.Add(new StarSlotViewModel { Index = i, StarSize = 24 });
            F95Stars.Add(new StarSlotViewModel { Index = i, StarSize = 28 });
        }
    }

    [ObservableProperty] private long _gameId;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string? _description;
    [ObservableProperty] private List<string> _tags = [];
    [ObservableProperty] private bool _isInstalled;
    [ObservableProperty] private string? _installPath;
    [ObservableProperty] private string? _archivePath;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private Bitmap? _cover;
    [ObservableProperty] private AnimatedMedia? _coverAnimation;
    [ObservableProperty] private Bitmap? _selectedScreenshot;
    [ObservableProperty] private AnimatedMedia? _selectedAnimation;
    [ObservableProperty] private bool _hasScreenshots;
    [ObservableProperty] private bool _isGalleryOpen;
    [ObservableProperty] private int _galleryIndex;
    [ObservableProperty] private Bitmap? _galleryImage;
    [ObservableProperty] private AnimatedMedia? _galleryAnimation;
    [ObservableProperty] private string _galleryCaption = "";
    [ObservableProperty] private string? _galleryFeedback;
    [ObservableProperty] private bool _canSetGalleryCover;
    [ObservableProperty] private bool _isCustomCover;
    [ObservableProperty] private bool _downloadsExpanded = true;
    [ObservableProperty] private bool _installExpanded = true;
    [ObservableProperty] private string _platformFilter = "All";
    [ObservableProperty] private string? _mediaStatus;

    public string DownloadsCollapsedHint => IsInstalled
        ? "Collapsed while installed · click to expand"
        : "Links from the F95 thread";
    public string DownloadsChevron => DownloadsExpanded ? "▾" : "▸";
    public string InstallCollapsedHint => IsInstalled
        ? $"Installed · {InstallPath}"
        : "Link a folder or extract an archive";
    public string InstallChevron => InstallExpanded ? "▾" : "▸";

    public ObservableCollection<DownloadLinkItemViewModel> AllLinks { get; } = [];
    public ObservableCollection<DownloadPackTabViewModel> DownloadPacks { get; } = [];
    [ObservableProperty] private DownloadPackTabViewModel? _selectedDownloadPack;
    public ObservableCollection<string> PlatformFilters { get; } = ["All", "Windows", "Linux", "PC", "Mac", "Android", "iOS", "Unknown"];
    public ObservableCollection<CloudSaveItemViewModel> Saves { get; } = [];
    public ObservableCollection<ScreenshotThumbViewModel> Screenshots { get; } = [];
    public ObservableCollection<PlayStatusPillViewModel> StatusPills { get; } = [];
    public ObservableCollection<StarSlotViewModel> YourStars { get; } = [];
    public ObservableCollection<StarSlotViewModel> F95Stars { get; } = [];
    private static readonly SemaphoreSlim ShotLoadGate = new(3, 3);
    [ObservableProperty] private string? _f95Url;
    [ObservableProperty] private string _playStatus = "unplayed";
    [ObservableProperty] private double? _userRating;
    [ObservableProperty] private double? _ratingHover;
    [ObservableProperty] private double? _f95Rating;
    [ObservableProperty] private string _f95RatingText = "—";
    [ObservableProperty] private string _userRatingText = "—";
    [ObservableProperty] private string _userNotes = "";
    [ObservableProperty] private string _playStatusBadgeLabel = "Unplayed";
    [ObservableProperty] private IBrush _playStatusBadgeBrush = new SolidColorBrush(Color.Parse("#3A4658"));
    [ObservableProperty] private IBrush _playStatusBadgeBorder = new SolidColorBrush(Color.Parse("#5A6A7E"));
    [ObservableProperty] private string _galleryHint = "No screenshots yet — Refresh metadata to pull the gallery onto the hub.";

    public async Task LoadAsync(long id)
    {
        GameId = id;
        Busy = true; Error = null; MediaStatus = null;
        Cover = null;
        CoverAnimation = null;
        SelectedScreenshot = null;
        Screenshots.Clear();
        HasScreenshots = false;
        GalleryHint = "Loading screenshots…";
        F95Url = null;
        AllLinks.Clear();
        DownloadPacks.Clear();
        SelectedDownloadPack = null;
        try
        {
            var detail = await _app.Hub.GetGameAsync(id);
            await ApplyDetailAsync(detail);
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { Busy = false; }
    }

    private async Task ApplyDetailAsync(GameDetail detail)
    {
        Title = detail.Game.Title;
        Meta = string.Join(" · ", new[]
        {
            detail.Game.Developer,
            detail.Game.Version,
            LibraryPaths.FormatPlaytime(detail.Game.PlaytimeSeconds)
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        Description = detail.Game.Description;
        Tags = TagHelpers.HumanTags(detail.Game.Tags);
        F95Url = detail.Game.F95Url;
        PlayStatus = NormalizePlayStatus(detail.Game.PlayStatus);
        UserRating = detail.Game.UserRating is > 0 ? detail.Game.UserRating : null;
        F95Rating = detail.Game.Rating is > 0 ? detail.Game.Rating : null;
        UserNotes = detail.Game.UserNotes ?? "";
        RefreshStatusPills();
        RefreshStars();
        Saves.Clear();
        foreach (var s in detail.Saves)
            Saves.Add(new CloudSaveItemViewModel { Save = s });
        var install = await _app.Database.GetInstallAsync(detail.Game.Id);
        IsInstalled = install is not null;
        InstallPath = install?.InstallPath;
        ArchivePath = null;
        DownloadsExpanded = !IsInstalled;
        InstallExpanded = !IsInstalled;
        IsCustomCover = detail.IsCustomCover;

        var coverCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(detail.CoverUrl)) coverCandidates.Add(detail.CoverUrl!);
        if (!string.IsNullOrWhiteSpace(detail.CoverFullUrl)) coverCandidates.Add(detail.CoverFullUrl!);
        if (coverCandidates.Count == 0)
            MediaStatus = "Hub returned no cover_url — try Refresh metadata.";
        await LoadCoverAsync(coverCandidates, detail.Game.UpdatedAt);
        if (Cover is null && coverCandidates.Count > 0)
            MediaStatus = _media.LastError ?? "Cover download/decode failed.";

        Screenshots.Clear();
        SelectedScreenshot = null;
        SelectedAnimation = null;
        IsGalleryOpen = false;
        var shotIndex = 0;
        foreach (var shot in detail.Screenshots)
        {
            // Match web: prefer hub cache, always keep F95 full_url as fallback source.
            var cached = string.IsNullOrWhiteSpace(shot.CachedUrl) ? null : shot.CachedUrl!.Replace('\\', '/');
            var full = string.IsNullOrWhiteSpace(shot.FullUrl) ? null : shot.FullUrl!.Replace('\\', '/');
            var primary = cached ?? full;
            if (string.IsNullOrWhiteSpace(primary)) continue;
            var fallback = cached is not null && full is not null
                && !string.Equals(cached, full, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
            var thumb = new ScreenshotThumbViewModel
            {
                Index = shotIndex++,
                Url = primary,
                FallbackUrl = fallback,
                CanSetCover = cached is not null
            };
            Screenshots.Add(thumb);
        }
        HasScreenshots = Screenshots.Count > 0;
        GalleryHint = HasScreenshots
            ? ""
            : "No screenshots yet — Refresh metadata downloads the gallery onto the hub.";
        if (!HasScreenshots)
            MediaStatus = (MediaStatus is null ? "" : MediaStatus + "\n") + GalleryHint;
        else
            _ = WarmScreenshotStripAsync();

        AllLinks.Clear();
        try
        {
            foreach (var n in DownloadLinkNormalizer.NormalizeAll(await _app.Hub.GetDownloadLinksAsync(detail.Game.Id)))
            {
                var item = new DownloadLinkItemViewModel
                {
                    Url = n.Url,
                    Host = n.Host,
                    Platform = n.Platform,
                    Title = n.Title,
                    DisplayName = n.DisplayName,
                    IsMasked = n.IsMasked
                };
                AllLinks.Add(item);
                _ = LoadHostIconAsync(item);
            }
            ApplyPlatformFilter();
            if (AllLinks.Count == 0)
                Status = "No hoster download links found on the F95 thread (hub may need F95 login).";
        }
        catch (Exception ex) { Status = "Download links unavailable: " + ex.Message; }
    }

    private async Task WarmScreenshotStripAsync()
    {
        // Eager-load the selected preview + a short thumb runway; rest load in background with a gate.
        if (Screenshots.Count == 0) return;
        await LoadShotAsync(Screenshots[0], preferAnimation: true);
        var runway = Math.Min(Screenshots.Count, 8);
        var tasks = new List<Task>();
        for (var i = 1; i < runway; i++)
            tasks.Add(LoadShotAsync(Screenshots[i], preferAnimation: false));
        await Task.WhenAll(tasks);
        for (var i = runway; i < Screenshots.Count; i++)
            _ = LoadShotAsync(Screenshots[i], preferAnimation: false);
    }

    private async Task LoadHostIconAsync(DownloadLinkItemViewModel item)
    {
        var favicon = BrandIcons.FaviconUrl(item.Host);
        if (favicon is null) return;
        var bmp = await _media.GetAsync(favicon);
        if (bmp is null) return;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => item.HostIcon = bmp);
    }

    private static string NormalizePlayStatus(string? status)
    {
        var s = (status ?? "unplayed").Trim().ToLowerInvariant();
        return s switch
        {
            "finished" or "complete" => "completed",
            "on_hold" or "on-hold" or "hold" => "playing",
            "playing" or "completed" or "dropped" or "unplayed" => s,
            _ => "unplayed"
        };
    }

    private void RefreshStatusPills()
    {
        foreach (var pill in StatusPills)
            pill.IsActive = string.Equals(pill.Value, PlayStatus, StringComparison.OrdinalIgnoreCase);
        var active = StatusPills.FirstOrDefault(p => p.IsActive);
        PlayStatusBadgeLabel = active?.Label ?? "Unplayed";
        PlayStatusBadgeBrush = active?.PillBrush ?? new SolidColorBrush(Color.Parse("#3A4658"));
        PlayStatusBadgeBorder = active?.PillBorder ?? new SolidColorBrush(Color.Parse("#5A6A7E"));
    }

    private void RefreshStars()
    {
        var yours = RatingHover ?? UserRating ?? 0;
        for (var i = 0; i < YourStars.Count; i++)
            YourStars[i].Fill = Math.Clamp(yours - i, 0, 1);
        UserRatingText = UserRating is > 0 ? UserRating.Value.ToString("0.0") : "—";

        var f95 = F95Rating ?? 0;
        for (var i = 0; i < F95Stars.Count; i++)
            F95Stars[i].Fill = Math.Clamp(f95 - i, 0, 1);
        F95RatingText = F95Rating is > 0 ? F95Rating.Value.ToString("0.0") : "—";
    }

    partial void OnRatingHoverChanged(double? value) => RefreshStars();
    partial void OnUserRatingChanged(double? value) => RefreshStars();
    partial void OnF95RatingChanged(double? value) => RefreshStars();
    partial void OnPlayStatusChanged(string value) => RefreshStatusPills();
    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(DownloadsCollapsedHint));
        OnPropertyChanged(nameof(InstallCollapsedHint));
    }
    partial void OnInstallPathChanged(string? value) => OnPropertyChanged(nameof(InstallCollapsedHint));
    partial void OnDownloadsExpandedChanged(bool value) => OnPropertyChanged(nameof(DownloadsChevron));
    partial void OnInstallExpandedChanged(bool value) => OnPropertyChanged(nameof(InstallChevron));
    partial void OnPlatformFilterChanged(string value) => ApplyPlatformFilter();
    partial void OnBusyChanged(bool value)
    {
        if (IsGalleryOpen && GalleryIndex >= 0 && GalleryIndex < Screenshots.Count)
            CanSetGalleryCover = Screenshots[GalleryIndex].CanSetCover && !value;
    }

    private void ApplyPlatformFilter()
    {
        var previousTitle = SelectedDownloadPack?.Title;
        DownloadPacks.Clear();
        IEnumerable<DownloadLinkItemViewModel> q = AllLinks;
        if (!string.Equals(PlatformFilter, "All", StringComparison.OrdinalIgnoreCase))
            q = AllLinks.Where(l => DownloadLinkNormalizer.MatchesFilter(l.Platform, PlatformFilter));

        DownloadPackTabViewModel? current = null;
        foreach (var link in q)
        {
            var header = string.IsNullOrWhiteSpace(link.Title) ? "Downloads" : link.Title!.Trim();
            if (current is null || !string.Equals(current.Title, header, StringComparison.OrdinalIgnoreCase))
            {
                current = new DownloadPackTabViewModel { Title = header };
                DownloadPacks.Add(current);
            }
            current.Links.Add(link);
        }

        SelectedDownloadPack = DownloadPacks.FirstOrDefault(p =>
                                    string.Equals(p.Title, previousTitle, StringComparison.OrdinalIgnoreCase))
                               ?? DownloadPacks.FirstOrDefault();
    }

    private async Task LoadShotAsync(ScreenshotThumbViewModel thumb, bool preferAnimation = false)
    {
        await ShotLoadGate.WaitAsync();
        try
        {
            AnimatedMedia? media = preferAnimation
                ? await _media.GetMediaAsync(thumb.Url)
                : null;
            if (media is null)
            {
                // Thumbs: still image only — avoids decoding every GIF animation up-front.
                var bmp = await _media.GetAsync(thumb.Url);
                if (bmp is null && !string.IsNullOrWhiteSpace(thumb.FallbackUrl))
                    bmp = await _media.GetAsync(thumb.FallbackUrl);
                if (bmp is null)
                {
                    if (_media.LastError is not null)
                        MediaStatus = _media.LastError;
                    return;
                }
                media = new AnimatedMedia { Preview = bmp };
            }
            else if (!string.IsNullOrWhiteSpace(thumb.FallbackUrl) && media.Preview is null)
            {
                media = await _media.GetMediaAsync(thumb.FallbackUrl) ?? media;
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                thumb.Image = media.Preview;
                thumb.Animation = media.IsAnimated ? media : null;
                if (SelectedScreenshot is null)
                {
                    SelectedScreenshot = media.Preview;
                    SelectedAnimation = media.IsAnimated ? media : null;
                }
                if (IsGalleryOpen && GalleryIndex == thumb.Index)
                {
                    GalleryImage = media.Preview;
                    GalleryAnimation = media.IsAnimated ? media : null;
                }
            });
        }
        finally
        {
            ShotLoadGate.Release();
        }
    }

    private async Task LoadCoverAsync(IEnumerable<string> urls, string? sourceVersion = null)
    {
        var request = MediaCacheRequest.Detail(sourceVersion);
        foreach (var url in urls.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var media = await _media.GetMediaAsync(url, request);
            if (media is null) continue;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Cover = media.Preview;
                CoverAnimation = media.IsAnimated ? media : null;
            });
            return;
        }
    }

    [RelayCommand]
    private void Back() => _back();

    [RelayCommand]
    private async Task TagClickAsync(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;
        await _onTagClick(tag);
    }

    [RelayCommand]
    private void OpenF95()
    {
        if (string.IsNullOrWhiteSpace(F95Url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(F95Url) { UseShellExecute = true });
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private void SelectScreenshot(ScreenshotThumbViewModel? shot)
    {
        if (shot is null) return;
        OpenGalleryAt(shot.Index);
    }

    [RelayCommand]
    private void OpenGallery()
    {
        if (!HasScreenshots) return;
        var idx = Screenshots.ToList().FindIndex(s => s.Image == SelectedScreenshot);
        OpenGalleryAt(idx >= 0 ? idx : 0);
    }

    [RelayCommand]
    private void CloseGallery()
    {
        IsGalleryOpen = false;
        GalleryAnimation = null;
        GalleryFeedback = null;
    }

    [RelayCommand]
    private void GalleryPrev()
    {
        if (Screenshots.Count == 0) return;
        OpenGalleryAt((GalleryIndex - 1 + Screenshots.Count) % Screenshots.Count);
    }

    [RelayCommand]
    private void GalleryNext()
    {
        if (Screenshots.Count == 0) return;
        OpenGalleryAt((GalleryIndex + 1) % Screenshots.Count);
    }

    private void OpenGalleryAt(int index)
    {
        if (Screenshots.Count == 0) return;
        index = Math.Clamp(index, 0, Screenshots.Count - 1);
        GalleryIndex = index;
        var shot = Screenshots[index];
        if (shot.Image is not null)
            SelectedScreenshot = shot.Image;
        SelectedAnimation = shot.Animation;
        GalleryImage = shot.Image;
        GalleryAnimation = shot.Animation;
        GalleryCaption = $"{index + 1} / {Screenshots.Count}"
            + (shot.IsAnimated ? " · GIF" : "");
        GalleryFeedback = null;
        CanSetGalleryCover = shot.CanSetCover && !Busy;
        for (var i = 0; i < Screenshots.Count; i++)
            Screenshots[i].IsSelected = i == index;
        IsGalleryOpen = true;
        if (shot.Animation is null)
            _ = LoadShotAsync(shot, preferAnimation: true);
    }

    [RelayCommand]
    private async Task SetCoverFromGalleryAsync()
    {
        if (!CanSetGalleryCover || Busy) return;
        var index = GalleryIndex;
        Busy = true; Error = null; GalleryFeedback = "Setting cover…";
        try
        {
            var detail = await _app.Hub.SetCoverFromScreenshotAsync(GameId, index);
            IsCustomCover = detail.IsCustomCover;

            // Prefer already-decoded gallery media (keeps GIF animation; avoids lightbox flash).
            if (index >= 0 && index < Screenshots.Count)
            {
                var shot = Screenshots[index];
                if (shot.Image is not null)
                {
                    Cover = shot.Image;
                    CoverAnimation = shot.Animation;
                }
            }

            var coverCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(detail.CoverUrl)) coverCandidates.Add(detail.CoverUrl!);
            if (!string.IsNullOrWhiteSpace(detail.CoverFullUrl)) coverCandidates.Add(detail.CoverFullUrl!);
            if (coverCandidates.Count > 0)
                await LoadCoverAsync(coverCandidates, detail.Game.UpdatedAt);

            Status = "Cover updated";
            GalleryFeedback = "Cover updated";
            _toasts.ShowRich("Cover updated", title: Title, cover: Cover, kind: ToastKind.Success);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            GalleryFeedback = null;
            _toasts.Error(ex.Message);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task ResetCoverAsync()
    {
        if (Busy) return;
        Busy = true; Error = null; GalleryFeedback = null;
        try
        {
            var detail = await _app.Hub.ResetCoverAsync(GameId);
            IsCustomCover = detail.IsCustomCover;
            var coverCandidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(detail.CoverUrl)) coverCandidates.Add(detail.CoverUrl!);
            if (!string.IsNullOrWhiteSpace(detail.CoverFullUrl)) coverCandidates.Add(detail.CoverFullUrl!);
            await LoadCoverAsync(coverCandidates, detail.Game.UpdatedAt);
            Status = "Cover reset";
            _toasts.ShowRich("Cover reset", title: Title, cover: Cover, kind: ToastKind.Success);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _toasts.Error(ex.Message);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private void ToggleDownloads() => DownloadsExpanded = !DownloadsExpanded;

    [RelayCommand]
    private void ToggleInstall() => InstallExpanded = !InstallExpanded;

    [RelayCommand]
    private async Task SyncSaveAsync(CloudSaveItemViewModel? item)
    {
        if (item is null || Busy) return;
        if (!IsInstalled || string.IsNullOrWhiteSpace(InstallPath))
        {
            Error = "Install the game on this PC before syncing a cloud save.";
            _toasts.Warning(Error);
            return;
        }

        Busy = true; Error = null;
        try
        {
            var destDir = RenpySaveSync.ResolveLocalSaveDirectory(InstallPath);
            var destPath = Path.Combine(destDir, item.Filename);
            if (File.Exists(destPath))
            {
                var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                var overwrite = await ConfirmDialog.ShowAsync(
                    owner,
                    "Overwrite save?",
                    $"A file named “{item.Filename}” already exists in:\n{destDir}\n\nOverwrite it with the cloud copy?",
                    "Overwrite");
                if (!overwrite) return;
            }

            var bytes = await _app.Hub.DownloadSaveAsync(GameId, item.Id);
            if (bytes is null || bytes.Length == 0)
                throw new InvalidOperationException("Hub returned an empty save file.");

            Directory.CreateDirectory(destDir);
            await File.WriteAllBytesAsync(destPath, bytes);
            Status = $"Synced {item.Filename}";
            _toasts.Success($"Save synced · {item.Filename}");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _toasts.Error(ex.Message);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task UploadSaveAsync()
    {
        if (Busy) return;
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Upload Ren'Py save",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Ren'Py saves") { Patterns = ["*.save", "*.sav"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            return;

        Busy = true; Error = null;
        try
        {
            var uploaded = await _app.Hub.UploadSaveAsync(GameId, path);
            await ReloadSavesAsync();
            Status = $"Uploaded {uploaded.Filename}";
            _toasts.Success($"Uploaded · {uploaded.Filename}");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _toasts.Error(ex.Message);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task DeleteSaveAsync(CloudSaveItemViewModel? item)
    {
        if (item is null || Busy) return;
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var ok = await ConfirmDialog.ShowAsync(
            owner,
            "Delete cloud save?",
            $"Delete “{item.Filename}” from the hub?\n\nThis does not remove the file from your local game folder.",
            "Delete");
        if (!ok) return;

        Busy = true; Error = null;
        try
        {
            await _app.Hub.DeleteSaveAsync(GameId, item.Id);
            Saves.Remove(item);
            Status = $"Deleted {item.Filename}";
            _toasts.Success($"Deleted · {item.Filename}");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _toasts.Error(ex.Message);
        }
        finally { Busy = false; }
    }

    private async Task ReloadSavesAsync()
    {
        var list = await _app.Hub.ListSavesAsync(GameId);
        Saves.Clear();
        foreach (var s in list)
            Saves.Add(new CloudSaveItemViewModel { Save = s });
    }

    [RelayCommand]
    private async Task SetPlayStatusAsync(string? status)
    {
        if (string.IsNullOrWhiteSpace(status) || Busy) return;
        var next = NormalizePlayStatus(status);
        var previous = PlayStatus;
        PlayStatus = next;
        Busy = true; Error = null;
        try
        {
            await _app.Hub.UpdateGameAsync(GameId, new UpdateGameUserData { PlayStatus = next });
            Status = "Status updated";
            _toasts.Success($"Status → {PlayStatusBadgeLabel}");
        }
        catch (Exception ex)
        {
            PlayStatus = previous;
            Error = ex.Message;
            _toasts.Error(ex.Message);
        }
        finally { Busy = false; }
    }

    public void PreviewRating(double? value) => RatingHover = value;

    [RelayCommand]
    private async Task SetUserRatingAsync(string? raw)
    {
        if (Busy) return;
        double? next = null;
        if (!string.IsNullOrWhiteSpace(raw) && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            next = Math.Clamp(Math.Round(parsed * 2) / 2.0, 0.5, 5);
        // Clicking the same value clears (web behavior).
        if (UserRating is { } current && next is { } n && Math.Abs(current - n) < 0.01)
            next = null;

        var previous = UserRating;
        UserRating = next;
        RatingHover = null;
        Busy = true; Error = null;
        try
        {
            await _app.Hub.UpdateGameAsync(GameId, new UpdateGameUserData
            {
                UserRating = next,
                ClearUserRating = next is null ? true : null
            });
            Status = next is null ? "Rating cleared" : "Rating saved";
            if (next is null) _toasts.Info("Rating cleared");
            else _toasts.Success("Rating saved");
        }
        catch (Exception ex)
        {
            UserRating = previous;
            Error = ex.Message;
            _toasts.Error(ex.Message);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task SaveNotesAsync()
    {
        Busy = true; Error = null; Status = "Saving…";
        try
        {
            await _app.Hub.UpdateGameAsync(GameId, new UpdateGameUserData { UserNotes = UserNotes });
            Status = "Notes saved";
            _toasts.Success("Notes saved");
        }
        catch (Exception ex) { Error = ex.Message; Status = null; _toasts.Error(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true; Error = null;
        Status = "Refreshing metadata from F95…";
        GalleryHint = "Refreshing metadata and caching screenshots…";
        _toasts.Info($"Refreshing · {Title}");
        try
        {
            await _app.Hub.RefreshGameAsync(GameId);
            await LoadAsync(GameId);
            Status = "Metadata refreshed";
            _toasts.Success("Metadata refreshed");
            // Background hub cache may still be writing screenshots — poll briefly.
            if (!HasScreenshots)
                _ = PollScreenshotsAfterRefreshAsync();
        }
        catch (Exception ex)
        {
            var msg = HubApiException.FriendlyMessage(ex, "Couldn't refresh metadata");
            Error = msg;
            Status = null;
            _toasts.Error(msg);
        }
        finally { Busy = false; }
    }

    private async Task PollScreenshotsAfterRefreshAsync()
    {
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(2500);
            try
            {
                var detail = await _app.Hub.GetGameAsync(GameId);
                var cached = detail.Screenshots.Count(s => !string.IsNullOrWhiteSpace(s.CachedUrl));
                if (detail.Screenshots.Count == 0) continue;
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (GameId != detail.Game.Id) return;
                    _ = ApplyDetailAsync(detail).ContinueWith(t =>
                    {
                        if (t.IsFaulted) return;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (HasScreenshots)
                                _toasts.Success(cached > 0
                                    ? $"Gallery ready · {Screenshots.Count} screenshots"
                                    : $"Gallery listed · {Screenshots.Count} images");
                        });
                    });
                });
                await Task.Delay(200);
                if (detail.Screenshots.Any(s => !string.IsNullOrWhiteSpace(s.CachedUrl)))
                    return;
                if (HasScreenshots && i >= 3)
                    return;
            }
            catch { /* keep polling */ }
        }
    }

    [RelayCommand]
    private async Task CheckVersionAsync()
    {
        if (Busy) return;
        Busy = true; Error = null; Status = "Checking F95 version…";
        _toasts.Info("Checking F95 version…");
        try
        {
            var result = await _app.Hub.CheckVersionAsync(GameId);
            Status = result.UpdateAvailable
                ? $"Update available: {result.StoredVersion ?? "?"} → {result.LatestVersion}"
                : $"Up to date ({result.LatestVersion}).";
            if (result.UpdateAvailable) _toasts.Warning(Status);
            else _toasts.Success(Status);
        }
        catch (Exception ex)
        {
            var msg = HubApiException.FriendlyMessage(ex, "Couldn't check version");
            Error = msg;
            Status = null;
            _toasts.Error(msg);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task DeleteFromLibraryAsync()
    {
        if (Busy) return;
        Busy = true; Error = null; Status = "Removing from hub library…";
        try
        {
            await _app.Hub.DeleteGameAsync(GameId);
            Status = "Removed from library.";
            _toasts.Success($"Removed · {Title}");
            _back();
        }
        catch (Exception ex)
        {
            var msg = HubApiException.FriendlyMessage(ex, "Couldn't remove from library");
            Error = msg;
            Status = null;
            _toasts.Error(msg);
        }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task DownloadLinkAsync(DownloadLinkItemViewModel? link)
    {
        if (link is null) return;
        Busy = true; Error = null;
        try
        {
            // Always queue through DownloadManager. Interactive/masked hosts open Afterglow Browser
            // and intercepted files are extracted into the library automatically.
            // Prefer existing install folder; otherwise Steam-like named folder under library root.
            var existing = await _app.Database.GetInstallAsync(GameId);
            var root = !string.IsNullOrWhiteSpace(existing?.InstallPath)
                ? existing!.InstallPath
                : LibraryPaths.InstallDirectory(_app.Preferences.LibraryRoot, GameId, Title);
            await _app.Downloads.QueueAsync(
                GameId,
                new Uri(link.Url),
                root,
                autoExtract: _app.Preferences.AutoExtract,
                gameTitle: Title);
            Status = $"Download queued ({link.Host}" + (link.Platform is null ? ")" : $" · {link.Platform})");
            _toasts.Info($"Queued · {Title}");
        }
        catch (Exception ex) { Error = ex.Message; _toasts.Error(ex.Message); }
        finally { Busy = false; }
    }

    public async Task PickInstallFolderAsync(TopLevel topLevel)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Link game install folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            InstallPath = path;
    }

    public async Task PickArchiveFileAsync(TopLevel topLevel)
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose game archive (.zip / .7z / .rar)",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Archives") { Patterns = ["*.zip", "*.7z", "*.rar", "*.tar", "*.gz"] },
                new FilePickerFileType("All files") { Patterns = ["*.*"] }
            ]
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            ArchivePath = path;
    }

    [RelayCommand]
    private async Task LinkFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(InstallPath) || !Directory.Exists(InstallPath))
        {
            Error = "Choose a valid install folder with Browse folder… first.";
            _toasts.Error(Error);
            return;
        }

        try
        {
            await _app.Downloads.LinkFolderAsync(GameId, InstallPath, Title);
            Status = "Linking folder…";
            _toasts.Info($"Queued · {Title}");
            _goDownloads();
        }
        catch (Exception ex) { Error = ex.Message; _toasts.Error(ex.Message); }
    }

    [RelayCommand]
    private async Task InstallArchiveAsync()
    {
        if (string.IsNullOrWhiteSpace(ArchivePath) || !File.Exists(ArchivePath))
        {
            Error = "Choose an archive file with Browse archive… (.zip / .7z / .rar).";
            _toasts.Error(Error);
            return;
        }
        if (!ArchiveExtractor.IsArchive(ArchivePath))
        {
            Error = "That file does not look like a supported archive.";
            _toasts.Error(Error);
            return;
        }

        try
        {
            var existing = await _app.Database.GetInstallAsync(GameId);
            var dest = !string.IsNullOrWhiteSpace(existing?.InstallPath)
                ? existing!.InstallPath
                : LibraryPaths.InstallDirectory(_app.Preferences.LibraryRoot, GameId, Title);
            await _app.Downloads.ImportLocalArchiveAsync(GameId, ArchivePath, dest, Title);
            Status = "Extracting archive…";
            _toasts.Info($"Queued · {Title}");
            _goDownloads();
        }
        catch (Exception ex) { Error = ex.Message; _toasts.Error(ex.Message); }
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        Busy = true; Error = null;
        try
        {
            var install = await _app.Database.GetInstallAsync(GameId)
                ?? throw new InvalidOperationException("Game is not installed on this PC.");
            var launchedAt = DateTimeOffset.UtcNow;
            await _app.Launcher.LaunchAsync(install);
            var playedSecs = Math.Max(0, (long)(DateTimeOffset.UtcNow - launchedAt).TotalSeconds);
            var synced = 0;
            GameSave? saved = null;
            try
            {
                synced = await _app.PlaytimeSync.FlushAsync();
                saved = await _app.RenpySaveSync.UploadNewestAsync(GameId, install.InstallPath);
            }
            catch (Exception syncEx)
            {
                _toasts.Warning("Play finished, but sync had an issue: " + syncEx.Message);
            }
            await LoadAsync(GameId);
            Status = $"Played {LibraryPaths.FormatPlaytime(playedSecs)}";
            _toasts.Success(Status + (synced > 0 ? " · playtime synced" : ""));
            if (saved is not null)
                _toasts.Success($"Save backed up · {saved.Filename}");
            else if (playedSecs >= 30)
                _toasts.Info("No save file found to back up");
        }
        catch (Exception ex) { Error = ex.Message; _toasts.Error(ex.Message); }
        finally { Busy = false; }
    }

    [RelayCommand]
    private async Task UninstallLocalAsync()
    {
        var install = await _app.Database.GetInstallAsync(GameId);
        if (install is null)
        {
            IsInstalled = false;
            return;
        }

        var owner = (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var underLibrary = install.InstallPath.StartsWith(_app.Preferences.LibraryRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                           || string.Equals(install.InstallPath.TrimEnd('\\', '/'), _app.Preferences.LibraryRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        var message = underLibrary
            ? $"Uninstall {Title}?\n\nThis removes the local install record and deletes files under:\n{install.InstallPath}"
            : $"Uninstall {Title}?\n\nThis folder is outside your Afterglow library:\n{install.InstallPath}\n\nOnly the install link will be removed — files will be kept.";

        var ok = await ConfirmDialog.ShowAsync(owner, "Uninstall game", message, "Uninstall");
        if (!ok) return;

        Busy = true; Error = null;
        try
        {
            if (underLibrary && Directory.Exists(install.InstallPath))
            {
                try { Directory.Delete(install.InstallPath, recursive: true); }
                catch (Exception ex)
                {
                    _toasts.Warning("Could not delete all files: " + ex.Message);
                }
            }
            await _app.Database.DeleteInstallAsync(GameId);
            IsInstalled = false;
            InstallPath = null;
            Status = underLibrary ? "Uninstalled — local files removed" : "Uninstalled — link removed (files kept)";
            _toasts.Success(Status);
        }
        catch (Exception ex) { Error = ex.Message; _toasts.Error(ex.Message); }
        finally { Busy = false; }
    }
}
