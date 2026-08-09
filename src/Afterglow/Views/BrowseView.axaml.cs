using Avalonia.Controls;
using Avalonia.Input;
using Afterglow.Core.Models;
using Afterglow.ViewModels;

namespace Afterglow.Views;

public partial class BrowseView : UserControl
{
    public BrowseView() => InitializeComponent();

    private void IncludeTag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tag }) return;
        if (DataContext is not BrowseViewModel vm) return;
        e.Handled = true;
        vm.RemoveIncludeTagCommand.Execute(tag);
    }

    private void ExcludeTag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string tag }) return;
        if (DataContext is not BrowseViewModel vm) return;
        e.Handled = true;
        vm.RemoveExcludeTagCommand.Execute(tag);
    }

    private void CatalogTag_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: CatalogTag tag }) return;
        if (DataContext is not BrowseViewModel vm) return;
        e.Handled = true;
        vm.ToggleCatalogTagCommand.Execute(tag);
    }
}
