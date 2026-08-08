using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Afterglow.Downloads;
using Afterglow.HubClient;
using Afterglow.HubSidecar;
using Afterglow.Launcher;
using Afterglow.LocalStore;
using Afterglow.Services;
using Afterglow.ViewModels;
using Afterglow.Views;
using Afterglow.BrowserHost;
using Microsoft.Extensions.DependencyInjection;

namespace Afterglow;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new LocalDatabase());
        services.AddSingleton(_ => new HubApiClient(new Uri("http://127.0.0.1:18080/")));
        services.AddSingleton<HubSidecarProcess>();
        services.AddSingleton(sp => new DownloadManager(sp.GetRequiredService<LocalDatabase>()));
        services.AddSingleton<IInteractiveDownloadBrowser, InteractiveDownloadBrowser>();
        services.AddSingleton(sp => new GameLauncher(sp.GetRequiredService<LocalDatabase>()));
        services.AddSingleton(sp => new PlaytimeSyncService(
            sp.GetRequiredService<LocalDatabase>(),
            sp.GetRequiredService<HubApiClient>()));
        services.AddSingleton(sp => new RenpySaveSync(sp.GetRequiredService<HubApiClient>()));
        services.AddSingleton<AfterglowAppService>();
        services.AddSingleton<MediaCacheService>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<MainViewModel>();
        Services = services.BuildServiceProvider();

        // Wire interactive browser + F95 cookie seeding for masked links.
        var downloads = Services.GetRequiredService<DownloadManager>();
        downloads.InteractiveBrowser = Services.GetRequiredService<IInteractiveDownloadBrowser>();
        downloads.SessionCookieProvider = async ct =>
        {
            try
            {
                var export = await Services.GetRequiredService<HubApiClient>().GetF95CookiesAsync(ct);
                return string.IsNullOrWhiteSpace(export.Cookies) ? null : export.Cookies;
            }
            catch
            {
                return null;
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<MainViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += (_, _) =>
            {
                Services.GetRequiredService<AfterglowAppService>().Dispose();
            };
            _ = vm.BootstrapAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
