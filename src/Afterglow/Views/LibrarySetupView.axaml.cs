using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Afterglow.ViewModels;

namespace Afterglow.Views;

public partial class LibrarySetupView : UserControl
{
    public LibrarySetupView() => InitializeComponent();

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LibrarySetupViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        await vm.PickFolderAsync(top);
    }
}
