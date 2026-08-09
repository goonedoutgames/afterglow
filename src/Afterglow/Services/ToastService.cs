using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Afterglow.Services;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error
}

public sealed class ToastItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Message { get; init; } = "";
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public Bitmap? Cover { get; init; }
    public ToastKind Kind { get; init; } = ToastKind.Info;
    public bool IsSticky { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public bool HasCover => Cover is not null;
    public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>App-wide transient notifications for feedback and significant events (not download progress).</summary>
public sealed class ToastService
{
    public ObservableCollection<ToastItem> Items { get; } = [];

    public void Show(string message, ToastKind kind = ToastKind.Info, int autoDismissMs = 6500) =>
        ShowRich(message, kind: kind, autoDismissMs: autoDismissMs);

    public void ShowRich(
        string message,
        string? title = null,
        string? subtitle = null,
        Bitmap? cover = null,
        ToastKind kind = ToastKind.Info,
        int autoDismissMs = 7500)
    {
        if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(title)) return;
        var item = new ToastItem
        {
            Message = (message ?? "").Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Subtitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim(),
            Cover = cover,
            Kind = kind
        };
        AddItem(item);

        if (autoDismissMs > 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(autoDismissMs);
                Dispatcher.UIThread.Post(() =>
                {
                    if (Items.Contains(item))
                        Items.Remove(item);
                });
            });
        }
    }

    public void Dismiss(ToastItem item)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Items.Remove(item);
            return;
        }
        Dispatcher.UIThread.Post(() => Items.Remove(item));
    }

    public void Info(string message) => Show(message, ToastKind.Info);
    public void Success(string message) => Show(message, ToastKind.Success);
    public void Warning(string message) => Show(message, ToastKind.Warning);
    public void Error(string message) => Show(message, ToastKind.Error, 10000);

    /// <summary>Toast that stays until <see cref="Dismiss"/>.</summary>
    public ToastItem ShowSticky(string message, string? title = null, Bitmap? cover = null, ToastKind kind = ToastKind.Info)
    {
        var item = new ToastItem
        {
            Message = (message ?? "").Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Cover = cover,
            Kind = kind,
            IsSticky = true
        };
        AddItem(item);
        return item;
    }

    public void DownloadComplete(string title, string? subtitle, Bitmap? cover) =>
        ShowRich("Ready to play", title: title, subtitle: subtitle ?? "Download complete", cover: cover, kind: ToastKind.Success, autoDismissMs: 9000);

    private void AddItem(ToastItem item)
    {
        void Add()
        {
            Items.Insert(0, item);
            // Prefer dropping non-sticky toasts so in-flight "Adding…" stays visible.
            while (Items.Count > 6)
            {
                var idx = -1;
                for (var i = Items.Count - 1; i >= 0; i--)
                {
                    if (!Items[i].IsSticky)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0) break;
                Items.RemoveAt(idx);
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) Add();
        else Dispatcher.UIThread.Post(Add);
    }
}
