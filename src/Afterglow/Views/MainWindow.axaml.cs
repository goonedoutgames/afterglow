using Avalonia.Controls;
using Avalonia.Input;
using Afterglow.Services;

namespace Afterglow.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                UpdateMaximizeGlyph();
        };
        Opened += async (_, _) =>
        {
            try
            {
                if (App.Services.GetService(typeof(AfterglowAppService)) is not AfterglowAppService app)
                    return;
                // Bootstrap may still be loading prefs; reload so placement is current.
                await app.ReloadPreferencesAsync();
                WindowPlacement.Apply(this, app.Preferences);
            }
            catch
            {
                // Keep XAML defaults.
            }
            finally
            {
                UpdateMaximizeGlyph();
            }
        };
        // Must be synchronous — Avalonia does not await async Closing handlers.
        Closing += (_, _) =>
        {
            try
            {
                if (App.Services.GetService(typeof(AfterglowAppService)) is not AfterglowAppService app)
                    return;
                var prefs = app.Preferences;
                WindowPlacement.Capture(this, prefs);
                Task.Run(() => app.SavePreferencesAsync(prefs)).GetAwaiter().GetResult();
            }
            catch
            {
                // Don't block close.
            }
        };
        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph()
    {
        if (this.FindControl<Button>("MaximizeButton") is { } btn)
            btn.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        Close();
}
