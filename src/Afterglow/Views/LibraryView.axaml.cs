using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Afterglow.ViewModels;

namespace Afterglow.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        vm.HydrateFromPrefs();
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is LibraryViewModel live)
            {
                live.HydrateFromPrefs();
                live.EnableSessionPersistence();
            }
        }, DispatcherPriority.Loaded);
    }

    private async void GameCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (sender is not Border { Tag: LibraryItemViewModel item }) return;
        if (DataContext is not LibraryViewModel vm) return;
        await vm.OpenGameCommand.ExecuteAsync(item);
    }

    private async void List_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (DataContext is not LibraryViewModel { SelectedGame: { } item } vm) return;
        await vm.OpenGameCommand.ExecuteAsync(item);
    }

    private void TagFilter_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: LibraryTagFilterItem item }) return;
        if (DataContext is not LibraryViewModel vm) return;
        e.Handled = true;
        vm.ToggleTagFilterCommand.Execute(item);
    }

    private async void GameCard_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Border { Tag: LibraryItemViewModel item }) return;
        if (DataContext is not LibraryViewModel vm) return;
        await vm.BeginHoverAsync(item);
    }

    private void TagOverflow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not LibraryViewModel vm) return;
        e.Handled = true;
        vm.ToggleTagOverflowCommand.Execute(null);
    }

    private void GameCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border { Tag: LibraryItemViewModel item })
            item.StopHoverPreview();
    }
}
