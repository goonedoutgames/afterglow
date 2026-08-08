using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Afterglow.Core;
using Afterglow.Core.Models;
using Afterglow.LocalStore;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Afterglow.Downloads;

public interface IHostResolver
{
    bool CanHandle(Uri url);
    Task<Uri> ResolveDirectUrlAsync(Uri url, CancellationToken cancellationToken = default);
}

public sealed class DirectHttpResolver : IHostResolver
{
    public bool CanHandle(Uri url) => url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps;
    public Task<Uri> ResolveDirectUrlAsync(Uri url, CancellationToken cancellationToken = default) => Task.FromResult(url);
}

public sealed class GoFileResolver(HttpClient? httpClient = null) : IHostResolver, IDisposable
{
    private readonly HttpClient _client = httpClient ?? new HttpClient();
    private readonly bool _ownsClient = httpClient is null;
    public bool CanHandle(Uri url) => url.Host.Contains("gofile.io", StringComparison.OrdinalIgnoreCase);
    public async Task<Uri> ResolveDirectUrlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var id = url.Segments.LastOrDefault()?.Trim('/');
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Invalid GoFile URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.gofile.io/contents/{id}");
        request.Headers.Add("Authorization", "Bearer 12345");
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var children = doc.RootElement.GetProperty("data").GetProperty("children");
        var first = children.EnumerateObject().FirstOrDefault().Value;
        var link = first.TryGetProperty("link", out var direct) ? direct.GetString() : null;
        return new Uri(link ?? throw new InvalidOperationException("GoFile did not return a downloadable file."));
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

/// <summary>Host cannot give a direct URL (captcha / ads). Caller should open the browser.</summary>
public sealed class NeedsBrowserException(Uri url, string host, string? detail = null)
    : Exception(detail ?? $"Open {host} in your browser to finish the download.")
{
    public Uri Url { get; } = url;
    public string Host { get; } = host;
}

public sealed class DataNodesResolver : IHostResolver
{
    public bool CanHandle(Uri url) => url.Host.Contains("datanodes.to", StringComparison.OrdinalIgnoreCase);

    public Task<Uri> ResolveDirectUrlAsync(Uri url, CancellationToken cancellationToken = default) =>
        Task.FromException<Uri>(new NeedsBrowserException(
            url,
            "datanodes",
            "DataNodes needs ads/captcha — finish them in Afterglow Browser; the download will be captured."));
}

/// <summary>
/// F95 masks external hosters behind /masked/{host}/... (captcha interstitial).
/// Download opens the masked URL in Afterglow Browser so the user can pass the interstitial.
/// </summary>
public sealed class F95MaskedResolver : IHostResolver
{
    public bool CanHandle(Uri url) =>
        url.Host.Contains("f95zone.to", StringComparison.OrdinalIgnoreCase)
        && (url.AbsolutePath.Contains("/masked/", StringComparison.OrdinalIgnoreCase)
            || url.AbsolutePath.Contains("masked-navigation", StringComparison.OrdinalIgnoreCase));

    public Task<Uri> ResolveDirectUrlAsync(Uri url, CancellationToken cancellationToken = default) =>
        Task.FromException<Uri>(new NeedsBrowserException(url, "f95-masked",
            "F95 masked link — complete the interstitial in Afterglow Browser; the hoster download will be captured."));
}

public sealed class MegaResolver : IHostResolver
{
    public bool CanHandle(Uri url) => url.Host.Contains("mega.nz", StringComparison.OrdinalIgnoreCase) || url.Host.Contains("mega.io", StringComparison.OrdinalIgnoreCase);
    public Task<Uri> ResolveDirectUrlAsync(Uri url, CancellationToken cancellationToken = default) =>
        Task.FromException<Uri>(new NeedsBrowserException(url, "mega",
            "MEGA needs Afterglow Browser. Complete the download there to capture the file."));
}

public sealed class PixeldrainResolver(HttpClient? httpClient = null) : IHostResolver, IDisposable
{
    private readonly HttpClient _client = httpClient ?? new HttpClient();
    private readonly bool _ownsClient = httpClient is null;
    public bool CanHandle(Uri url) => url.Host.Contains("pixeldrain.com", StringComparison.OrdinalIgnoreCase);
    public async Task<Uri> ResolveDirectUrlAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var segments = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(segments, x => x is "u" or "l");
        if (index < 0 || index == segments.Length - 1) throw new InvalidOperationException("Invalid Pixeldrain URL.");
        var kind = segments[index] == "l" ? "list" : "file";
        var id = segments[index + 1];
        using var response = await _client.GetAsync($"https://pixeldrain.com/api/{kind}/{id}/info", cancellationToken);
        response.EnsureSuccessStatusCode();
        return new Uri($"https://pixeldrain.com/api/{kind}/{id}/download");
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

public static class ArchiveExtractor
{
    public static bool IsArchive(string path) => new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz" }.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    public readonly record struct ExtractProgress(long EntriesDone, long EntriesTotal, long BytesDone, long BytesTotal);

    public static async Task ExtractAsync(
        string archivePath,
        string destination,
        Func<ExtractProgress, Task>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destination);
        // Keep archive open while writing entries (SharpCompress streams from the archive).
        await Task.Factory.StartNew(async () =>
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            var entriesTotal = entries.Count;
            var bytesTotal = entries.Sum(e => Math.Max(0L, e.Size));
            long bytesDone = 0;

            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i];
                entry.WriteToDirectory(destination, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                bytesDone += Math.Max(0L, entry.Size);
                if (onProgress is not null)
                    await onProgress(new ExtractProgress(i + 1, entriesTotal, bytesDone, bytesTotal)).ConfigureAwait(false);
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>Legacy overload without progress.</summary>
    public static Task ExtractAsync(string archivePath, string destination, CancellationToken cancellationToken) =>
        ExtractAsync(archivePath, destination, onProgress: null, cancellationToken);
}

public static class ExeDetector
{
    public static string? FindExecutable(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        var exes = Directory.EnumerateFiles(directory, "*.exe", SearchOption.AllDirectories)
            .Where(x => !Path.GetFileName(x).Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar))
            .ToList();
        return exes.Count > 0 ? exes[0] : null;
    }
}

public sealed class DownloadManager : IDisposable
{
    private readonly LocalDatabase _database;
    private readonly HttpClient _client;
    private readonly IReadOnlyList<IHostResolver> _resolvers;
    private readonly SemaphoreSlim _concurrency;
    private readonly bool _ownsClient;
    public IInteractiveDownloadBrowser? InteractiveBrowser { get; set; }
    /// <summary>Optional provider for F95 (or other) session cookies to seed Afterglow Browser.</summary>
    public Func<CancellationToken, Task<string?>>? SessionCookieProvider { get; set; }
    public event EventHandler<DownloadJob>? JobChanged;
    public event EventHandler<Guid>? JobRemoved;
    public event EventHandler? FinishedJobsCleared;

    public DownloadManager(LocalDatabase database, int maxConcurrentDownloads = 2, HttpClient? httpClient = null, IEnumerable<IHostResolver>? resolvers = null)
    {
        _database = database;
        _client = httpClient ?? new HttpClient();
        _ownsClient = httpClient is null;
        _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrentDownloads));
        _resolvers = resolvers?.ToList() ??
        [
            new F95MaskedResolver(),
            new GoFileResolver(_client),
            new MegaResolver(),
            new PixeldrainResolver(_client),
            new DataNodesResolver(),
            new DirectHttpResolver()
        ];
    }
    public async Task<DownloadJob> QueueAsync(
        long gameId,
        Uri sourceUrl,
        string installDirectory,
        string? version = null,
        bool autoExtract = true,
        string? gameTitle = null,
        CancellationToken cancellationToken = default)
    {
        var job = new DownloadJob
        {
            GameId = gameId,
            SourceUrl = sourceUrl.ToString(),
            Host = sourceUrl.Host,
            GameTitle = string.IsNullOrWhiteSpace(gameTitle) ? null : gameTitle.Trim()
        };
        await _database.UpsertDownloadJobAsync(job, cancellationToken);
        JobChanged?.Invoke(this, job);
        _ = ProcessAsync(job, installDirectory, version, autoExtract, cancellationToken);
        return job;
    }

    public async Task RemoveJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _database.DeleteDownloadJobAsync(id, cancellationToken);
        JobRemoved?.Invoke(this, id);
    }

    public async Task ClearFinishedAsync(CancellationToken cancellationToken = default)
    {
        await _database.ClearFinishedDownloadJobsAsync(cancellationToken);
        FinishedJobsCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Import a local archive into the library (shows under Downloads with extract progress).</summary>
    public async Task<DownloadJob> ImportLocalArchiveAsync(
        long gameId,
        string archivePath,
        string installDirectory,
        string? gameTitle = null,
        CancellationToken cancellationToken = default)
    {
        var job = new DownloadJob
        {
            GameId = gameId,
            SourceUrl = archivePath,
            Host = "archive",
            GameTitle = string.IsNullOrWhiteSpace(gameTitle) ? null : gameTitle.Trim(),
            Status = DownloadJobStatus.Queued
        };
        await SaveAsync(job, cancellationToken);
        _ = RunLocalArchiveAsync(job, archivePath, installDirectory, cancellationToken);
        return job;
    }

    /// <summary>Link an existing game folder (same Downloads list UX as installs).</summary>
    public async Task<DownloadJob> LinkFolderAsync(
        long gameId,
        string folderPath,
        string? gameTitle = null,
        CancellationToken cancellationToken = default)
    {
        var job = new DownloadJob
        {
            GameId = gameId,
            SourceUrl = folderPath,
            Host = "folder",
            GameTitle = string.IsNullOrWhiteSpace(gameTitle) ? null : gameTitle.Trim(),
            Status = DownloadJobStatus.Queued
        };
        await SaveAsync(job, cancellationToken);
        _ = RunLinkFolderAsync(job, folderPath, cancellationToken);
        return job;
    }

    /// <summary>Import a file already on disk into the library.</summary>
    public async Task<DownloadJob> ImportLocalFileAsync(long gameId, string filePath, string installDirectory, bool autoExtract = true, string? gameTitle = null, CancellationToken cancellationToken = default)
    {
        if (autoExtract && ArchiveExtractor.IsArchive(filePath))
            return await ImportLocalArchiveAsync(gameId, filePath, installDirectory, gameTitle, cancellationToken);

        var job = new DownloadJob
        {
            GameId = gameId,
            SourceUrl = filePath,
            Host = "local",
            GameTitle = gameTitle,
            Status = DownloadJobStatus.Queued
        };
        await SaveAsync(job, cancellationToken);
        _ = Task.Run(async () =>
        {
            try
            {
                await FinishFromLocalFileAsync(job, filePath, installDirectory, version: null, autoExtract: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                job.Status = DownloadJobStatus.Failed;
                job.Error = ex.Message;
                await SaveAsync(job, CancellationToken.None);
            }
        }, cancellationToken);
        return job;
    }

    private async Task RunLocalArchiveAsync(DownloadJob job, string archivePath, string installDirectory, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(installDirectory);
            job.Status = DownloadJobStatus.Extracting;
            job.Progress = 0;
            job.Error = null;
            await SaveAsync(job, cancellationToken);
            await FinishFromLocalFileAsync(job, archivePath, installDirectory, version: null, autoExtract: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            job.Status = DownloadJobStatus.Cancelled;
            job.Error = null;
            await SaveAsync(job, CancellationToken.None);
        }
        catch (Exception ex)
        {
            job.Status = DownloadJobStatus.Failed;
            job.Error = ex.Message;
            await SaveAsync(job, CancellationToken.None);
        }
    }

    private async Task RunLinkFolderAsync(DownloadJob job, string folderPath, CancellationToken cancellationToken)
    {
        try
        {
            job.Status = DownloadJobStatus.Extracting;
            job.Progress = 0.35;
            job.Error = null;
            await SaveAsync(job, cancellationToken);

            if (!Directory.Exists(folderPath))
                throw new DirectoryNotFoundException("Install folder was not found.");

            var exe = ExeDetector.FindExecutable(folderPath);
            job.Progress = 0.75;
            await SaveAsync(job, cancellationToken);

            await _database.UpsertInstallAsync(new LocalInstall
            {
                GameId = job.GameId,
                InstallPath = folderPath,
                ExePath = exe
            }, cancellationToken);

            job.Status = DownloadJobStatus.Completed;
            job.Progress = 1;
            job.OutputPath = folderPath;
            job.Error = null;
            await SaveAsync(job, cancellationToken);
        }
        catch (Exception ex)
        {
            job.Status = DownloadJobStatus.Failed;
            job.Error = ex.Message;
            await SaveAsync(job, CancellationToken.None);
        }
    }

    private async Task ProcessAsync(DownloadJob job, string installDirectory, string? version, bool autoExtract, CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken);
        var holdingSlot = true;
        try
        {
            job.Status = DownloadJobStatus.Resolving;
            job.Error = null;
            await SaveAsync(job, cancellationToken);
            var source = new Uri(job.SourceUrl);
            var resolver = _resolvers.FirstOrDefault(x => x.CanHandle(source)) ?? throw new NotSupportedException($"No resolver supports {source.Host}.");
            Uri direct;
            try
            {
                direct = await resolver.ResolveDirectUrlAsync(source, cancellationToken);
            }
            catch (NeedsBrowserException browser)
            {
                holdingSlot = false;
                _concurrency.Release();
                await CaptureViaInteractiveBrowserAsync(job, browser.Url, installDirectory, version, autoExtract, browser.Message, cancellationToken);
                return;
            }

            // Single request: HTML → Afterglow Browser handoff; otherwise stream into the library.
            using var request = new HttpRequestMessage(HttpMethod.Get, direct);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                holdingSlot = false;
                _concurrency.Release();
                await CaptureViaInteractiveBrowserAsync(job, source, installDirectory, version, autoExtract,
                    $"Hoster needs interaction ({source.Host}). Opening Afterglow Browser.", cancellationToken);
                return;
            }

            await StreamResponseToTempAndFinishAsync(job, response, suggestedName: null, installDirectory, version, autoExtract, cancellationToken);
        }
        catch (OperationCanceledException) { job.Status = DownloadJobStatus.Cancelled; job.Error = null; await SaveAsync(job, CancellationToken.None); }
        catch (Exception ex) { job.Status = DownloadJobStatus.Failed; job.Error = ex.Message; await SaveAsync(job, CancellationToken.None); }
        finally
        {
            if (holdingSlot) _concurrency.Release();
        }
    }

    private async Task CaptureViaInteractiveBrowserAsync(
        DownloadJob job, Uri url, string installDirectory, string? version, bool autoExtract, string? hint, CancellationToken cancellationToken)
    {
        if (InteractiveBrowser is null)
        {
            OpenInSystemBrowser(url);
            job.Status = DownloadJobStatus.OpenedInBrowser;
            job.Error = hint ?? "Opened in system browser (Afterglow Browser unavailable).";
            await SaveAsync(job, CancellationToken.None);
            return;
        }

        job.Status = DownloadJobStatus.OpenedInBrowser;
        job.Error = null;
        await SaveAsync(job, cancellationToken);

        string? seedCookies = null;
        string? seedDomain = null;
        // Seed whenever we have hub F95 cookies — masked links and redirects need them.
        if (SessionCookieProvider is not null)
        {
            try
            {
                seedCookies = await SessionCookieProvider(cancellationToken);
                if (!string.IsNullOrWhiteSpace(seedCookies))
                    seedDomain = ".f95zone.to";
            }
            catch
            {
                // Browser still opens; user may already be logged in via WebView profile.
            }
        }

        var handoff = await InteractiveBrowser.CaptureDownloadAsync(url, seedCookies, seedDomain, cancellationToken);
        if (handoff is null)
        {
            job.Status = DownloadJobStatus.Cancelled;
            job.Error = "Afterglow Browser closed before a download started.";
            await SaveAsync(job, CancellationToken.None);
            return;
        }

        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            await DownloadToTempAndFinishAsync(
                job, handoff.DirectUrl, handoff.CookieHeader, handoff.Referer, handoff.UserAgent,
                handoff.SuggestedFileName, installDirectory, version, autoExtract, cancellationToken);
        }
        catch (OperationCanceledException) { job.Status = DownloadJobStatus.Cancelled; job.Error = null; await SaveAsync(job, CancellationToken.None); }
        catch (Exception ex) { job.Status = DownloadJobStatus.Failed; job.Error = ex.Message; await SaveAsync(job, CancellationToken.None); }
        finally { _concurrency.Release(); }
    }

    private async Task DownloadToTempAndFinishAsync(
        DownloadJob job,
        Uri direct,
        string? cookieHeader,
        string? referer,
        string? userAgent,
        string? suggestedName,
        string installDirectory,
        string? version,
        bool autoExtract,
        CancellationToken cancellationToken)
    {
        job.Status = DownloadJobStatus.Downloading;
        job.Error = null;
        job.Progress = 0;
        job.BytesReceived = 0;
        job.BytesPerSecond = 0;
        await SaveAsync(job, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, direct);
        if (!string.IsNullOrWhiteSpace(cookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        if (!string.IsNullOrWhiteSpace(referer))
            request.Headers.TryAddWithoutValidation("Referer", referer);
        if (!string.IsNullOrWhiteSpace(userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hoster handed off an HTML page instead of a file. Retry and wait for the real download to start.");

        await StreamResponseToTempAndFinishAsync(job, response, suggestedName, installDirectory, version, autoExtract, cancellationToken);
    }

    private async Task StreamResponseToTempAndFinishAsync(
        DownloadJob job,
        HttpResponseMessage response,
        string? suggestedName,
        string installDirectory,
        string? version,
        bool autoExtract,
        CancellationToken cancellationToken)
    {
        job.Status = DownloadJobStatus.Downloading;
        job.Error = null;
        await SaveAsync(job, cancellationToken);

        Directory.CreateDirectory(AppPaths.DownloadsTemp);
        var downloadName = SanitizeFileName(suggestedName);
        var cd = response.Content.Headers.ContentDisposition?.FileNameStar
                 ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
        if (!string.IsNullOrWhiteSpace(cd))
            downloadName = SanitizeFileName(cd);
        if (string.IsNullOrWhiteSpace(downloadName) || !downloadName.Contains('.'))
        {
            var fromUrl = SanitizeFileName(Path.GetFileName(Uri.UnescapeDataString(response.RequestMessage?.RequestUri?.AbsolutePath ?? "")));
            if (!string.IsNullOrWhiteSpace(fromUrl) && fromUrl.Contains('.'))
                downloadName = fromUrl;
        }
        if (string.IsNullOrWhiteSpace(downloadName) || !downloadName.Contains('.'))
            downloadName = "download.bin";

        var output = Path.Combine(AppPaths.DownloadsTemp, $"{job.Id:N}_{downloadName}");
        if (File.Exists(output))
        {
            try { File.Delete(output); }
            catch { output = Path.Combine(AppPaths.DownloadsTemp, $"{job.Id:N}_{Guid.NewGuid():N}_{downloadName}"); }
        }

        var length = response.Content.Headers.ContentLength;
        job.TotalBytes = length;
        var lastSave = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long received = 0;

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                job.BytesReceived = received;
                job.Progress = length is > 0 ? (double)received / length.Value : 0;
                var elapsed = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
                job.BytesPerSecond = received / elapsed;
                if ((DateTime.UtcNow - lastSave).TotalMilliseconds >= 250)
                {
                    lastSave = DateTime.UtcNow;
                    await SaveAsync(job, cancellationToken);
                }
            }
            await file.FlushAsync(cancellationToken);
        }

        await FinishFromLocalFileAsync(job, output, installDirectory, version, autoExtract, cancellationToken);
    }

    private async Task FinishFromLocalFileAsync(
        DownloadJob job, string output, string installDirectory, string? version, bool autoExtract, CancellationToken cancellationToken)
    {
        var finalDirectory = installDirectory;
        if (autoExtract && ArchiveExtractor.IsArchive(output))
        {
            job.Status = DownloadJobStatus.Extracting;
            job.Progress = 0;
            await SaveAsync(job, cancellationToken);

            var lastSave = DateTime.UtcNow;
            await ArchiveExtractor.ExtractAsync(output, installDirectory, async progress =>
            {
                if (progress.BytesTotal > 0)
                {
                    job.TotalBytes = progress.BytesTotal;
                    job.BytesReceived = progress.BytesDone;
                    job.Progress = Math.Clamp((double)progress.BytesDone / progress.BytesTotal, 0, 0.99);
                }
                else if (progress.EntriesTotal > 0)
                {
                    // No reliable sizes — track by file count (UI labels these as files, not bytes).
                    job.TotalBytes = progress.EntriesTotal;
                    job.BytesReceived = progress.EntriesDone;
                    job.Progress = Math.Clamp((double)progress.EntriesDone / progress.EntriesTotal, 0, 0.99);
                }

                if ((DateTime.UtcNow - lastSave).TotalMilliseconds < 200 && progress.EntriesDone < progress.EntriesTotal)
                    return;
                lastSave = DateTime.UtcNow;
                await SaveAsync(job, cancellationToken);
            }, cancellationToken);
        }
        else
        {
            Directory.CreateDirectory(installDirectory);
            var destFile = Path.Combine(installDirectory, Path.GetFileName(output));
            if (!string.Equals(Path.GetFullPath(output), Path.GetFullPath(destFile), StringComparison.OrdinalIgnoreCase))
                File.Copy(output, destFile, overwrite: true);
            finalDirectory = installDirectory;
        }

        job.Status = DownloadJobStatus.Completed;
        job.Progress = 1;
        job.Error = null;
        job.OutputPath = finalDirectory;
        await SaveAsync(job, cancellationToken);
        await _database.UpsertInstallAsync(new LocalInstall
        {
            GameId = job.GameId,
            InstallPath = finalDirectory,
            ExePath = ExeDetector.FindExecutable(finalDirectory),
            InstalledVersion = version
        }, cancellationToken);
    }

    private static void OpenInSystemBrowser(Uri url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.ToString()) { UseShellExecute = true });
        }
        catch { /* toast/job message still informs the user */ }
    }

    private async Task SaveAsync(DownloadJob job, CancellationToken ct) { await _database.UpsertDownloadJobAsync(job, ct); JobChanged?.Invoke(this, job); }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "download.bin";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public void Dispose() { _concurrency.Dispose(); if (_ownsClient) _client.Dispose(); foreach (var resolver in _resolvers.OfType<IDisposable>()) resolver.Dispose(); }
}
