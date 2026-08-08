using Avalonia.Controls;
using Avalonia.Interactivity;
using Afterglow.ViewModels;

namespace Afterglow.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void BrowseLibrary_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        await vm.PickLibraryFolderAsync(top);
    }
}
