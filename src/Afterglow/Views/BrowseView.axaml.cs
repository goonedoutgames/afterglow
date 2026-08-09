using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Afterglow.Core.Models;
using Afterglow.ViewModels;

namespace Afterglow.Views;

public partial class BrowseView : UserControl
{
    private bool _restoringScroll;
    private bool _pendingRestore;
    private int _restorePasses;

    public BrowseView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e) => _pendingRestore = true;

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _pendingRestore = true;
        _restorePasses = 0;
        // Layout must finish (ItemsControl measure) before Offset sticks.
        Dispatcher.UIThread.Post(() =>
            Dispatcher.UIThread.Post(RestoreScroll, DispatcherPriority.Loaded),
            DispatcherPriority.Background);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => PersistScroll();

    private void PersistScroll()
    {
        if (_restoringScroll) return;
        if (DataContext is not BrowseViewModel vm) return;
        // Preview hides the grid; don't clobber the remembered catalog offset.
        if (vm.IsPreviewOpen) return;
        var y = ResultsScroller.Offset.Y;
        if (y > 0 || vm.ScrollOffsetY <= 0)
            vm.ScrollOffsetY = y;
    }

    private void RestoreScroll()
    {
        if (!_pendingRestore) return;
        if (DataContext is not BrowseViewModel vm) return;
        if (vm.IsPreviewOpen || vm.ScrollOffsetY <= 0)
        {
            _pendingRestore = false;
            return;
        }

        _restoringScroll = true;
        ResultsScroller.Offset = new Vector(ResultsScroller.Offset.X, vm.ScrollOffsetY);
        _restorePasses++;

        // Re-apply once after measure; Avalonia often clamps Offset before content height exists.
        if (_restorePasses < 2)
        {
            Dispatcher.UIThread.Post(RestoreScroll, DispatcherPriority.Background);
            return;
        }

        _pendingRestore = false;
        Dispatcher.UIThread.Post(() => _restoringScroll = false, DispatcherPriority.Background);
    }

    private void ResultsScroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_restoringScroll) return;
        PersistScroll();
    }

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

    private void PreviewShot_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: CatalogPreviewShotViewModel shot }) return;
        if (DataContext is not BrowseViewModel vm) return;
        e.Handled = true;
        vm.SelectPreviewShotCommand.Execute(shot);
    }
}
