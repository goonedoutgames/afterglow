using System.Diagnostics;
using Afterglow.Core;

namespace Afterglow.HubSidecar;

/// <summary>Owns an embedded AVN Hub process for local-mode callers.</summary>
public sealed class HubSidecarProcess : IDisposable
{
    public const int Port = 18080;
    private Process? _process;
    public bool IsRunning => _process is { HasExited: false };
    public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
    public string? LastExecutable { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (IsRunning) return;
        AppPaths.EnsureDirectories();
        progress?.Report("Preparing local avn-hub sidecar…");
        var executable = await SidecarBootstrap.EnsureAsync(progress, cancellationToken);
        LastExecutable = executable;

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };
        startInfo.Environment["AVN_HUB_DATA_DIR"] = AppPaths.HubDataDir;
        startInfo.Environment["AVN_HUB_API_HOST"] = "127.0.0.1";
        startInfo.Environment["AVN_HUB_API_PORT"] = Port.ToString();
        startInfo.Environment["AVN_HUB_WEB_HOST"] = "127.0.0.1";
        startInfo.Environment["AVN_HUB_WEB_PORT"] = "18081";
        startInfo.Environment["AVN_HUB_PUBLIC_API_URL"] = $"http://127.0.0.1:{Port}";
        startInfo.Environment["AVN_HUB_CORS_ORIGINS"] = "*";
        progress?.Report("Starting local hub…");
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the AVN Hub sidecar.");
        await WaitForHealthAsync(cancellationToken);
        progress?.Report("Local hub is healthy.");
    }

    private async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var health = new Uri(BaseAddress, "api/v1/health");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning) throw new InvalidOperationException("The AVN Hub sidecar exited before becoming ready.");
            try
            {
                using var response = await client.GetAsync(health, cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(250, cancellationToken);
        }
        throw new TimeoutException("The AVN Hub sidecar did not become healthy in time.");
    }

    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();
}
