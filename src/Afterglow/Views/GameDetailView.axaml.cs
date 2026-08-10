using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Afterglow.ViewModels;

namespace Afterglow.Views;

public partial class GameDetailView : UserControl
{
    private GameDetailViewModel? _wiredVm;

    public GameDetailView()
    {
        InitializeComponent();
        // TabControl / focus changes raise BringIntoView and yank the page ScrollViewer
        // down to downloads when packs finish loading.
        AddHandler(RequestBringIntoViewEvent, OnRequestBringIntoView, RoutingStrategies.Bubble | RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_wiredVm is not null)
            _wiredVm.PropertyChanged -= OnVmPropertyChanged;
        _wiredVm = DataContext as GameDetailViewModel;
        if (_wiredVm is not null)
        {
            _wiredVm.PropertyChanged += OnVmPropertyChanged;
            ResetDetailScroll();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Only reset when opening another game — not on Refresh metadata (Busy flicker).
        if (e.PropertyName == nameof(GameDetailViewModel.GameId))
            ResetDetailScroll();
    }

    private void ResetDetailScroll()
    {
        if (DetailScroller is null) return;
        DetailScroller.Offset = new Vector(DetailScroller.Offset.X, 0);
    }

    private static void OnRequestBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        // Page scroll stays user-driven (mouse wheel / scrollbar).
        e.Handled = true;
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GameDetailViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        await vm.PickInstallFolderAsync(top);
    }

    private async void BrowseArchive_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GameDetailViewModel vm) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        await vm.PickArchiveFileAsync(top);
    }

    private void Screenshot_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: ScreenshotThumbViewModel shot }) return;
        if (DataContext is not GameDetailViewModel vm) return;
        e.Handled = true;
        vm.SelectScreenshotCommand.Execute(shot);
        Focus();
    }

    private void Preview_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not GameDetailViewModel vm || !vm.HasScreenshots) return;
        e.Handled = true;
        vm.OpenGalleryCommand.Execute(null);
        Focus();
    }

    private void ThumbStrip_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        // Vertical wheel → horizontal strip scroll (don’t bubble to page ScrollViewer).
        var delta = e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X;
        if (delta == 0) return;
        sv.Offset = new Avalonia.Vector(sv.Offset.X - delta * 48, 0);
        e.Handled = true;
    }

    private void Gallery_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not GameDetailViewModel vm || !vm.IsGalleryOpen) return;
        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseGalleryCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left:
                vm.GalleryPrevCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right:
                vm.GalleryNextCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void LightboxBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Dismiss only when clicking empty chrome (not image / buttons / strip).
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DataContext is not GameDetailViewModel vm) return;
        vm.CloseGalleryCommand.Execute(null);
        e.Handled = true;
    }

    private void StarHit_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is not GameDetailViewModel vm) return;
        if (sender is not Control { Tag: string raw }) return;
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return;
        vm.PreviewRating(value);
    }

    private void StarHit_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is GameDetailViewModel vm)
            vm.PreviewRating(null);
    }
}
