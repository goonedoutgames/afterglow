using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Afterglow.Services;

public enum ConfirmDialogResult
{
    Primary,
    Secondary,
    Cancel
}

public static class ConfirmDialog
{
    public static async Task<bool> ShowAsync(Window? owner, string title, string message, string confirmLabel = "OK")
    {
        var result = await ShowChoicesAsync(owner, title, message, confirmLabel, cancelLabel: "Cancel");
        return result == ConfirmDialogResult.Primary;
    }

    public static async Task<ConfirmDialogResult> ShowChoicesAsync(
        Window? owner,
        string title,
        string message,
        string primaryLabel,
        string? secondaryLabel = null,
        string cancelLabel = "Cancel")
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Background = new SolidColorBrush(Color.Parse("#161A22"))
        };

        var result = ConfirmDialogResult.Cancel;
        var primary = new Button
        {
            Content = primaryLabel,
            Classes = { "accent" },
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        primary.Click += (_, _) => { result = ConfirmDialogResult.Primary; dialog.Close(); };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var cancel = new Button
        {
            Content = cancelLabel,
            Classes = { "ghost" },
            MinWidth = 96
        };
        cancel.Click += (_, _) => dialog.Close();
        buttons.Children.Add(cancel);

        if (!string.IsNullOrWhiteSpace(secondaryLabel))
        {
            var secondary = new Button
            {
                Content = secondaryLabel,
                Classes = { "ghost" },
                MinWidth = 96
            };
            secondary.Click += (_, _) => { result = ConfirmDialogResult.Secondary; dialog.Close(); };
            buttons.Children.Add(secondary);
        }

        buttons.Children.Add(primary);

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
                    buttons
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
