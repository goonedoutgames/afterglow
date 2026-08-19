using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Afterglow.Core;

namespace Afterglow.Services;

/// <summary>Downloads Afterglow-Setup-x64.exe and launches the Inno Setup wizard after this process exits.</summary>
public static class AppUpdateInstaller
{
    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"Afterglow/{AppVersionInfo.Current} (+https://github.com/{AppUpdateAssets.Owner}/{AppUpdateAssets.Repo})");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/octet-stream,*/*");
        return client;
    }

    public static async Task<string?> DownloadAsync(
        Window? owner,
        AppUpdateInfo update,
        CancellationToken cancellationToken = default)
    {
        var url = string.IsNullOrWhiteSpace(update.InstallerUrl)
            ? AppUpdateAssets.DirectInstallerUrl(update.TagName)
            : update.InstallerUrl!;

        var dir = Path.Combine(Path.GetTempPath(), "Afterglow", "updates");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"Afterglow-Setup-{update.Version}.exe");

        var status = new TextBlock
        {
            Text = "Connecting to GitHub…",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, IsIndeterminate = true };
        var cancel = new Button { Content = "Cancel", Classes = { "ghost" }, MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Right };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var finished = false;
        cancel.Click += (_, _) => cts.Cancel();

        var dialog = new Window
        {
            Title = "Downloading update",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#161A22")),
            Content = new Border
            {
                Padding = new Avalonia.Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock { Text = $"Downloading Afterglow {update.Version}", FontSize = 18, FontWeight = FontWeight.SemiBold },
                        status,
                        bar,
                        cancel
                    }
                }
            }
        };

        dialog.Closing += (_, _) =>
        {
            if (!finished)
                cts.Cancel();
        };

        var download = DownloadToFileAsync(DownloadHttp, url, dest, (got, total) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (total is > 0)
                {
                    bar.IsIndeterminate = false;
                    bar.Value = Math.Clamp(got / (double)total.Value, 0, 1);
                    status.Text = $"{FormatBytes(got)} / {FormatBytes(total.Value)}";
                }
                else
                {
                    bar.IsIndeterminate = true;
                    status.Text = $"Downloaded {FormatBytes(got)}";
                }
            });
        }, cts.Token);

        var shown = owner is not null ? dialog.ShowDialog(owner) : ShowModeless(dialog);
        string? path = null;
        Exception? error = null;
        try
        {
            path = await download;
        }
        catch (OperationCanceledException)
        {
            TryDelete(dest);
        }
        catch (Exception ex)
        {
            error = ex;
            TryDelete(dest);
        }

        if (error is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { dialog.Close(); } catch { /* already closed */ }
            });
            try { await shown; } catch { /* ignore */ }
            throw error;
        }

        finished = path is not null;
        Dispatcher.UIThread.Post(() =>
        {
            try { dialog.Close(); } catch { /* already closed */ }
        });
        try { await shown; } catch { /* ignore */ }

        return path;
    }

    public static void LaunchWizardAndExit(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Installer was not downloaded.", installerPath);

        var cmd = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = cmd,
            Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{installerPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        System.Diagnostics.Process.Start(psi);

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    private static async Task ShowModeless(Window dialog)
    {
        dialog.Show();
        var tcs = new TaskCompletionSource();
        dialog.Closed += (_, _) => tcs.TrySetResult();
        await tcs.Task;
    }

    private static async Task<string> DownloadToFileAsync(
        HttpClient http,
        string url,
        string dest,
        Action<long, long?> progress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var snippet = body.Length > 80 ? body[..80] : body;
            throw new HttpRequestException($"Installer download HTTP {(int)resp.StatusCode}: {snippet}");
        }

        var total = resp.Content.Headers.ContentLength;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 82_000, useAsync: true);
        var buffer = new byte[82_000];
        long got = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            got += n;
            progress(got, total);
        }

        if (got < 1_000_000)
            throw new InvalidOperationException("Downloaded installer was too small — GitHub may not have published Afterglow-Setup-x64.exe yet.");

        return dest;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.00} MB"
    };
}
