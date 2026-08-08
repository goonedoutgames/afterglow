using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Afterglow.Services;

namespace Afterglow.Controls;

/// <summary>Shows a static bitmap, or plays Magick-decoded GIF frames when available.</summary>
public sealed class AnimatedImageView : Image
{
    public static readonly StyledProperty<AnimatedMedia?> MediaProperty =
        AvaloniaProperty.Register<AnimatedImageView, AnimatedMedia?>(nameof(Media));

    public static readonly StyledProperty<Bitmap?> FallbackSourceProperty =
        AvaloniaProperty.Register<AnimatedImageView, Bitmap?>(nameof(FallbackSource));

    private readonly DispatcherTimer _timer;
    private int _frameIndex;

    public AnimatedImageView()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => AdvanceFrame();
    }

    public AnimatedMedia? Media
    {
        get => GetValue(MediaProperty);
        set => SetValue(MediaProperty, value);
    }

    public Bitmap? FallbackSource
    {
        get => GetValue(FallbackSourceProperty);
        set => SetValue(FallbackSourceProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MediaProperty || change.Property == FallbackSourceProperty
            || change.Property == IsVisibleProperty)
        {
            Restart();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Restart()
    {
        _timer.Stop();
        _frameIndex = 0;
        var media = Media;
        if (media is { IsAnimated: true } && IsVisible)
        {
            Source = media.Frames[0];
            _timer.Interval = TimeSpan.FromMilliseconds(media.DelayMs(0));
            _timer.Start();
            return;
        }

        Source = media?.Preview ?? FallbackSource;
    }

    private void AdvanceFrame()
    {
        var media = Media;
        if (media is not { IsAnimated: true } || media.Frames.Count == 0)
        {
            _timer.Stop();
            return;
        }

        _frameIndex = (_frameIndex + 1) % media.Frames.Count;
        Source = media.Frames[_frameIndex];
        _timer.Interval = TimeSpan.FromMilliseconds(media.DelayMs(_frameIndex));
    }
}
