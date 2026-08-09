using Afterglow.Core.Models;
using Avalonia;
using Avalonia.Controls;

namespace Afterglow.Services;

public static class WindowPlacement
{
    public static void Apply(Window window, UiPreferences prefs)
    {
        if (prefs.WindowWidth is >= 1000 and <= 8000)
            window.Width = prefs.WindowWidth.Value;
        if (prefs.WindowHeight is >= 640 and <= 8000)
            window.Height = prefs.WindowHeight.Value;

        if (prefs.WindowX is int x && prefs.WindowY is int y)
        {
            var pos = new PixelPoint(x, y);
            if (IsOnAnyScreen(window, pos))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = pos;
            }
        }

        if (prefs.WindowMaximized)
            window.WindowState = WindowState.Maximized;
    }

    public static void Capture(Window window, UiPreferences prefs)
    {
        prefs.WindowMaximized = window.WindowState == WindowState.Maximized;
        // Avalonia keeps restore Width/Height/Position while maximized on Win32.
        if (window.Width is >= 1000 and <= 8000)
            prefs.WindowWidth = window.Width;
        if (window.Height is >= 640 and <= 8000)
            prefs.WindowHeight = window.Height;
        prefs.WindowX = window.Position.X;
        prefs.WindowY = window.Position.Y;
    }

    private static bool IsOnAnyScreen(Window window, PixelPoint pos)
    {
        try
        {
            var screens = window.Screens?.All;
            if (screens is null || screens.Count == 0)
                return true;
            foreach (var screen in screens)
            {
                var b = screen.WorkingArea;
                if (pos.X + 80 > b.X && pos.X < b.X + b.Width && pos.Y + 40 > b.Y && pos.Y < b.Y + b.Height)
                    return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }
}
