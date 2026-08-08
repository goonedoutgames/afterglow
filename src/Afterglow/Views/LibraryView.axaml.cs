using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Afterglow.ViewModels;

namespace Afterglow.Views;

public partial class LibraryView : UserControl
{
    public LibraryView() => InitializeComponent();

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
}
