using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace Afterglow;

sealed class Program
{
    private const string AppUserModelId = "GoonedOutGames.Afterglow";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    [STAThread]
    public static void Main(string[] args)
    {
        // Keep Start Menu / taskbar identity stable across portable vs Inno installs.
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* non-Windows or old shell */ }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
