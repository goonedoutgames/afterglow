using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;
using Afterglow.Core;
using Afterglow.Downloads;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Afterglow.BrowserHost;

/// <summary>
/// Minimal Afterglow WebView2 host: finish captchas/timers/interstitials, then hand the
/// direct URL to DownloadManager and close. No ad-blocking — hosters need their own pages.
/// </summary>
public sealed class InteractiveDownloadBrowser : IInteractiveDownloadBrowser
{
    public Task<BrowserHandoff?> CaptureDownloadAsync(
        Uri url,
        string? seedCookieHeader = null,
        string? seedCookieDomain = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Afterglow Browser currently requires Windows (WebView2).");

        var tcs = new TaskCompletionSource<BrowserHandoff?>(TaskCreationOptions.RunContinuationsAsynchronously);
        AfterglowBrowserForm? form = null;
        CancellationTokenRegistration reg = default;
        if (cancellationToken.CanBeCanceled)
        {
            reg = cancellationToken.Register(() =>
            {
                tcs.TrySetResult(null);
                try
                {
                    var f = form;
                    if (f is { IsDisposed: false })
                        f.BeginInvoke(f.Close);
                }
                catch
                {
                    // Form already gone.
                }
            });
        }

        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var browserForm = new AfterglowBrowserForm(url, seedCookieHeader, seedCookieDomain, tcs);
                form = browserForm;
                browserForm.FormClosed += (_, _) => Application.ExitThread();
                Application.Run(browserForm);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                form = null;
                reg.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "AfterglowBrowser"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class AfterglowBrowserForm : Form
{
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new()
    {
        Dock = DockStyle.Bottom,
        Height = 28,
        TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(10, 0, 0, 0),
        Text = "Complete captcha/timer here. When the file starts, Afterglow takes over and this window closes."
    };
    private readonly TextBox _address = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TaskCompletionSource<BrowserHandoff?> _completion;
    private readonly Uri _startUrl;
    private readonly string? _seedCookieHeader;
    private readonly string? _seedCookieDomain;
    private bool _completed;

    public AfterglowBrowserForm(
        Uri startUrl,
        string? seedCookieHeader,
        string? seedCookieDomain,
        TaskCompletionSource<BrowserHandoff?> completion)
    {
        _startUrl = startUrl;
        _seedCookieHeader = seedCookieHeader;
        _seedCookieDomain = seedCookieDomain;
        _completion = completion;

        Text = "Afterglow Browser";
        Width = 1100;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(0x0C, 0x0F, 0x14);
        ForeColor = Color.WhiteSmoke;
        ShowInTaskbar = true;
        TryApplyAppIcon();

        var top = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8, 6, 8, 6) };
        var go = new Button { Text = "Go", Dock = DockStyle.Right, Width = 64 };
        go.Click += (_, _) => NavigateAddress();
        top.Controls.Add(_address);
        top.Controls.Add(go);

        _status.BackColor = Color.FromArgb(0x16, 0x1A, 0x22);
        _status.ForeColor = Color.FromArgb(0xB0, 0xB8, 0xC4);
        _address.Text = startUrl.ToString();

        Controls.Add(_webView);
        Controls.Add(_status);
        Controls.Add(top);

        Shown += async (_, _) => await InitializeAsync();
        FormClosed += (_, _) =>
        {
            if (!_completed)
                _completion.TrySetResult(null);
        };
    }

    private void TryApplyAppIcon()
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "afterglow.ico"),
                Path.Combine(AppContext.BaseDirectory, "afterglow.ico"),
                Path.Combine(AppPaths.Root, "..", "Assets", "afterglow.ico")
            };
            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                Icon = new Icon(path);
                return;
            }
        }
        catch { /* default icon */ }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var userData = Path.Combine(AppPaths.Root, "webview2-profile");
            Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userData);
            await _webView.EnsureCoreWebView2Async(env);

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = true;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;

            SeedCookies(_webView.CoreWebView2);
            AttachCookieHeaderInjection(_webView.CoreWebView2);

            _webView.CoreWebView2.DownloadStarting += OnDownloadStarting;
            _webView.CoreWebView2.NavigationCompleted += (_, args) =>
            {
                _address.Text = _webView.Source?.ToString() ?? _address.Text;
                if (!args.IsSuccess)
                    _status.Text = "Navigation issue — use the address bar or retry the hoster link.";
            };

            _webView.CoreWebView2.Navigate(_startUrl.ToString());
            _status.Text = string.IsNullOrWhiteSpace(_seedCookieHeader)
                ? "Ready — finish hoster steps. Download starts → Afterglow takes over."
                : "Ready (F95 session loaded) — finish interstitial/hoster steps.";
        }
        catch (Exception ex)
        {
            _status.Text = "WebView2 failed to start: " + ex.Message;
            MessageBox.Show(this,
                "Afterglow Browser needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message,
                "Afterglow Browser",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            _completion.TrySetResult(null);
            Close();
        }
    }

    private void SeedCookies(CoreWebView2 core)
    {
        if (string.IsNullOrWhiteSpace(_seedCookieHeader)) return;
        var domain = string.IsNullOrWhiteSpace(_seedCookieDomain)
            ? DeriveCookieDomain(_startUrl.Host)
            : _seedCookieDomain!;
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { domain };
        if (domain.StartsWith('.'))
            domains.Add(domain.TrimStart('.'));
        else
            domains.Add("." + domain);
        // Always include the F95 apex when seeding an F95 session.
        domains.Add(".f95zone.to");
        domains.Add("f95zone.to");

        foreach (var part in _seedCookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(name)) continue;
            foreach (var d in domains)
            {
                try
                {
                    var cookie = core.CookieManager.CreateCookie(name, value, d, "/");
                    cookie.IsSecure = _startUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                                     || d.Contains("f95zone", StringComparison.OrdinalIgnoreCase);
                    core.CookieManager.AddOrUpdateCookie(cookie);
                }
                catch
                {
                    // Keep seeding remaining cookies.
                }
            }
        }
    }

    /// <summary>
    /// CookieManager alone is flaky on first navigation to masked links.
    /// Inject the stored Cookie header on every F95 request as a backup.
    /// </summary>
    private void AttachCookieHeaderInjection(CoreWebView2 core)
    {
        if (string.IsNullOrWhiteSpace(_seedCookieHeader)) return;
        try
        {
            core.AddWebResourceRequestedFilter("*://f95zone.to/*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("*://*.f95zone.to/*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                try
                {
                    var headers = e.Request.Headers;
                    if (headers.Contains("Cookie"))
                    {
                        var existing = headers.GetHeader("Cookie") ?? "";
                        if (!existing.Contains(_seedCookieHeader!, StringComparison.Ordinal))
                            headers.SetHeader("Cookie", MergeCookieHeaders(existing, _seedCookieHeader!));
                    }
                    else
                    {
                        headers.SetHeader("Cookie", _seedCookieHeader);
                    }
                }
                catch
                {
                    // Ignore injection failures for individual requests.
                }
            };
        }
        catch
        {
            // Filter API unavailable — CookieManager seed is the only path.
        }
    }

    private static string MergeCookieHeaders(string existing, string seed)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string header)
        {
            foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }
        Add(existing);
        Add(seed);
        return string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static string DeriveCookieDomain(string host)
    {
        host = host.Trim().TrimStart('.');
        if (host.EndsWith("f95zone.to", StringComparison.OrdinalIgnoreCase))
            return ".f95zone.to";
        var parts = host.Split('.');
        return parts.Length >= 2 ? "." + string.Join('.', parts[^2..]) : host;
    }

    private void NavigateAddress()
    {
        if (Uri.TryCreate(_address.Text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            _webView.CoreWebView2?.Navigate(uri.ToString());
    }

    private async void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            e.Cancel = true;
            e.Handled = true;

            var direct = new Uri(e.DownloadOperation.Uri);
            var name = Sanitize(Path.GetFileName(e.ResultFilePath));
            if (string.IsNullOrWhiteSpace(name) || name is "download" or "download.bin")
                name = Sanitize(Path.GetFileName(Uri.UnescapeDataString(direct.AbsolutePath)));
            if (string.IsNullOrWhiteSpace(name) || !name.Contains('.'))
                name = $"download-{Guid.NewGuid():N}.bin";

            _status.Text = $"Handing off {name} to Afterglow…";

            string? cookieHeader = null;
            try
            {
                var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync(direct.GetLeftPart(UriPartial.Authority));
                if (cookies.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var c in cookies)
                    {
                        if (sb.Length > 0) sb.Append("; ");
                        sb.Append(c.Name).Append('=').Append(c.Value);
                    }
                    cookieHeader = sb.ToString();
                }
            }
            catch { /* proceed without cookies */ }

            string? userAgent = null;
            try { userAgent = _webView.CoreWebView2.Settings.UserAgent; } catch { /* optional */ }

            Complete(new BrowserHandoff
            {
                DirectUrl = direct,
                CookieHeader = cookieHeader,
                Referer = _webView.Source?.ToString() ?? _startUrl.ToString(),
                UserAgent = userAgent,
                SuggestedFileName = name
            });
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Complete(BrowserHandoff handoff)
    {
        if (_completed) return;
        _completed = true;
        _completion.TrySetResult(handoff);
        if (IsHandleCreated)
            BeginInvoke(Close);
        else
            Close();
    }

    private static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
