using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Afterglow.Services;

public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window? owner, string title, string message, string confirmLabel = "OK")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#161A22"))
        };

        var result = false;
        var ok = new Button
        {
            Content = confirmLabel,
            Classes = { "accent" },
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Classes = { "ghost" },
            MinWidth = 96,
            Margin = new Avalonia.Thickness(0, 0, 8, 0)
        };
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();

        dialog.Content = new Border
        {
            Padding = new Avalonia.Thickness(20),
            Child = new StackPanel
            {
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = title,
                        FontSize = 18,
                        FontWeight = FontWeight.SemiBold
                    },
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.85,
                        LineHeight = 22
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 0,
                        Children = { cancel, ok }
                    }
                }
            }
        };

        if (owner is not null)
            await dialog.ShowDialog(owner);
        else
        {
            dialog.Show();
            var tcs = new TaskCompletionSource();
            dialog.Closed += (_, _) => tcs.TrySetResult();
            await tcs.Task;
        }
        return result;
    }
}
