using Afterglow.Core;
using Afterglow.Core.Models;
using Afterglow.Downloads;
using Afterglow.HubClient;
using Afterglow.HubSidecar;
using Afterglow.Launcher;
using Afterglow.LocalStore;

namespace Afterglow.Services;

/// <summary>
/// Owns exclusive Remote/Local connection. Remote never starts the sidecar.
/// </summary>
public sealed class AfterglowAppService : IDisposable
{
    private readonly LocalDatabase _db;
    private readonly HubApiClient _hub;
    private readonly HubSidecarProcess _sidecar;
    private readonly object _gate = new();

    public AfterglowAppService(
        LocalDatabase db,
        HubApiClient hub,
        HubSidecarProcess sidecar,
        DownloadManager downloads,
        GameLauncher launcher,
        PlaytimeSyncService playtimeSync,
        RenpySaveSync renpySaveSync)
    {
        _db = db;
        _hub = hub;
        _sidecar = sidecar;
        Downloads = downloads;
        Launcher = launcher;
        PlaytimeSync = playtimeSync;
        RenpySaveSync = renpySaveSync;
        AppPaths.EnsureDirectories();
    }

    public LocalDatabase Database => _db;
    public HubApiClient Hub => _hub;
    public HubSidecarProcess Sidecar => _sidecar;
    public DownloadManager Downloads { get; }
    public GameLauncher Launcher { get; }
    public PlaytimeSyncService PlaytimeSync { get; }
    public RenpySaveSync RenpySaveSync { get; }
    public AppConnectionConfig Connection { get; private set; } = new();
    public UiPreferences Preferences { get; private set; } = new() { LibraryRoot = AppPaths.DefaultLibraryRoot };
    public bool IsLocalMode => Connection.Mode == BackendMode.Local;
    public bool IsConfigured => Connection.Mode is BackendMode.Remote or BackendMode.Local;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        Connection = await _db.GetConnectionConfigAsync(ct);
        Preferences = await _db.GetUiPreferencesAsync(ct);
        var hadStoredRoot = !string.IsNullOrWhiteSpace(Preferences.LibraryRoot);
        if (!hadStoredRoot)
            Preferences.LibraryRoot = AppPaths.DefaultLibraryRoot;

        // Heal prefs wiped by older card-scale saves that rewrote the whole row.
        if (!Preferences.LibrarySetupComplete)
        {
            var hasInstalls = false;
            try { hasInstalls = (await _db.GetInstallsAsync(ct)).Count > 0; }
            catch { /* ignore */ }

            if (hasInstalls || (hadStoredRoot && Directory.Exists(Preferences.LibraryRoot)))
            {
                Preferences.LibrarySetupComplete = true;
                await _db.SaveUiPreferencesAsync(Preferences, ct);
            }
        }

        if (Preferences.LibrarySetupComplete)
            AppPaths.EnsureDirectories(Preferences.LibraryRoot);

        if (Connection.Mode == BackendMode.Local)
            await StartLocalAsync(null, ct);
        else if (Connection.Mode == BackendMode.Remote && !string.IsNullOrWhiteSpace(Connection.RemoteApiBase))
            ConfigureRemoteClient(Connection.RemoteApiBase!, Connection.AuthToken);
    }

    public async Task ConfigureRemoteAsync(string apiBase, string? password, CancellationToken ct = default)
    {
        StopSidecar();
        // Clear any previous token so BaseAddress/auth can be reconfigured after prior requests.
        ConfigureRemoteClient(apiBase, null);
        await _hub.HealthAsync(ct);

        if (!string.IsNullOrWhiteSpace(password))
        {
            await _hub.LoginAsync(password, ct);
        }
        else
        {
            var me = await _hub.MeAsync(ct);
            if (me.Configured && !me.Authenticated)
                throw new InvalidOperationException("This hub requires a password.");
        }

        Connection = new AppConnectionConfig
        {
            Mode = BackendMode.Remote,
            RemoteApiBase = apiBase.TrimEnd('/'),
            AuthToken = _hub.BearerToken,
            ClientId = Connection.ClientId
        };
        await _db.SaveConnectionConfigAsync(Connection, ct);
    }

    public async Task ConfigureLocalAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await StartLocalAsync(progress, ct);
        Connection = new AppConnectionConfig
        {
            Mode = BackendMode.Local,
            RemoteApiBase = _sidecar.BaseAddress.ToString().TrimEnd('/'),
            AuthToken = _hub.BearerToken,
            ClientId = Connection.ClientId
        };
        await _db.SaveConnectionConfigAsync(Connection, ct);
    }

    public Task SwitchToRemoteAsync(string apiBase, string? password, CancellationToken ct = default) =>
        ConfigureRemoteAsync(apiBase, password, ct);

    public Task SwitchToLocalAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        ConfigureLocalAsync(progress, ct);

    public async Task EnsureSidecarAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await SidecarBootstrap.EnsureAsync(progress, ct);
    }

    public async Task SavePreferencesAsync(UiPreferences prefs, CancellationToken ct = default)
    {
        Preferences = prefs;
        AppPaths.EnsureDirectories(prefs.LibraryRoot);
        await _db.SaveUiPreferencesAsync(prefs, ct);
    }

    /// <summary>Updates card scale only — avoids clobbering LibrarySetupComplete / LibraryRoot.</summary>
    public async Task SaveLibraryCardScaleAsync(double scale, CancellationToken ct = default)
    {
        scale = Math.Clamp(scale, 0.75, 2.0);
        Preferences.LibraryCardScale = scale;
        await _db.UpdateLibraryCardScaleAsync(scale, ct);
    }

    /// <summary>Clears hub connection + UI prefs so First-run can choose Local/Remote again. Keeps local installs.</summary>
    public async Task ResetToFactoryAsync(CancellationToken ct = default)
    {
        StopSidecar();
        await _db.ResetConnectionAsync(ct);
        Preferences = new UiPreferences
        {
            LibraryRoot = AppPaths.DefaultLibraryRoot,
            LibrarySetupComplete = false,
            AccentHex = UiPreferences.DefaultAccentHex
        };
        await _db.SaveUiPreferencesAsync(Preferences, ct);
        await _db.ClearDownloadJobsAsync(ct);
        await _db.ClearPendingPlaySessionsAsync(ct);
        Connection = await _db.GetConnectionConfigAsync(ct);
        _hub.Configure(new Uri("http://127.0.0.1:18080/"), null);

        try
        {
            var cache = AppPaths.MediaCache;
            if (Directory.Exists(cache))
                Directory.Delete(cache, recursive: true);
        }
        catch { /* best effort */ }

        AppPaths.EnsureDirectories();
    }

    private async Task StartLocalAsync(IProgress<string>? progress, CancellationToken ct)
    {
        await _sidecar.StartAsync(ct, progress);
        _hub.Configure(_sidecar.BaseAddress, null);
        await _hub.HealthAsync(ct);
    }

    private void ConfigureRemoteClient(string apiBase, string? token) =>
        _hub.Configure(new Uri(apiBase.TrimEnd('/') + "/"), token);

    private void StopSidecar()
    {
        lock (_gate) _sidecar.Stop();
    }

    public void Dispose()
    {
        if (Connection.Mode == BackendMode.Local)
            StopSidecar();
    }
}
