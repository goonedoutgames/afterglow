using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Afterglow.Controls;

/// <summary>
/// Ellipsized title that marquee-scrolls when the parent game card is hovered.
/// </summary>
public sealed class MarqueeText : Border
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeText, string?>(nameof(Text));

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<MarqueeText, double>(nameof(FontSize), 13);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<MarqueeText, FontWeight>(nameof(FontWeight), FontWeight.SemiBold);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<MarqueeText, IBrush?>(nameof(Foreground));

    private readonly TextBlock _text;
    private readonly TranslateTransform _translate = new();
    private Control? _hoverSource;
    private DispatcherTimer? _timer;
    private double _overflow;
    private double _offset;
    private bool _scrolling;
    private int _pauseTicks;

    public MarqueeText()
    {
        ClipToBounds = true;
        Height = 20;
        _text = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            RenderTransform = _translate
        };
        Child = _text;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
            _text.Text = change.GetNewValue<string?>() ?? "";
        else if (change.Property == FontSizeProperty)
        {
            var size = change.GetNewValue<double>();
            _text.FontSize = size;
            Height = Math.Max(18, size + 4);
        }
        else if (change.Property == FontWeightProperty)
            _text.FontWeight = change.GetNewValue<FontWeight>();
        else if (change.Property == ForegroundProperty)
            _text.Foreground = change.GetNewValue<IBrush?>();
        else if (change.Property == BoundsProperty)
            MeasureOverflow();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _text.Text = Text ?? "";
        _text.FontSize = FontSize;
        _text.FontWeight = FontWeight;
        if (Foreground is not null) _text.Foreground = Foreground;
        Height = Math.Max(18, FontSize + 4);

        _hoverSource = this.FindAncestorOfType<Border>(false);
        while (_hoverSource is not null && !_hoverSource.Classes.Contains("game-card-host"))
            _hoverSource = _hoverSource.FindAncestorOfType<Border>(false);

        if (_hoverSource is null) _hoverSource = this;
        _hoverSource.PointerEntered += OnHoverEnter;
        _hoverSource.PointerExited += OnHoverExit;
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        StopScroll(reset: true);
        if (_hoverSource is not null)
        {
            _hoverSource.PointerEntered -= OnHoverEnter;
            _hoverSource.PointerExited -= OnHoverExit;
            _hoverSource = null;
        }
    }

    private void OnHoverEnter(object? sender, PointerEventArgs e)
    {
        MeasureOverflow();
        if (_overflow <= 2) return;
        _scrolling = true;
        _pauseTicks = 10;
        EnsureTimer();
    }

    private void OnHoverExit(object? sender, PointerEventArgs e)
    {
        StopScroll(reset: true);
    }

    private void MeasureOverflow()
    {
        if (Bounds.Width <= 0)
        {
            _overflow = 0;
            return;
        }

        var trimming = _text.TextTrimming;
        _text.TextTrimming = TextTrimming.None;
        _text.Measure(new Size(double.PositiveInfinity, Bounds.Height));
        _overflow = Math.Max(0, _text.DesiredSize.Width - Bounds.Width);
        if (!_scrolling)
            _text.TextTrimming = trimming;
    }

    private void EnsureTimer()
    {
        if (_timer is null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(1000.0 / 60.0)
            };
            _timer.Tick += OnTick;
        }

        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private void StopScroll(bool reset)
    {
        _scrolling = false;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        if (!reset) return;
        _offset = 0;
        _translate.X = 0;
        _text.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_scrolling)
        {
            StopScroll(reset: false);
            return;
        }

        _text.TextTrimming = TextTrimming.None;
        MeasureOverflow();
        if (_overflow <= 2)
        {
            StopScroll(reset: true);
            return;
        }

        if (_pauseTicks > 0)
        {
            _pauseTicks--;
            return;
        }

        _offset += 0.9;
        if (_offset >= _overflow)
        {
            _offset = _overflow;
            _translate.X = -_offset;
            _pauseTicks = 24;
            // After pause, jump back to start for another pass.
            _offset = -0.01; // sentinel; next non-pause tick resets
            return;
        }

        if (_offset < 0)
        {
            _offset = 0;
            _translate.X = 0;
            _pauseTicks = 18;
            return;
        }

        _translate.X = -_offset;
    }
}
