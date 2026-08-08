using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace Afterglow.Controls;

/// <summary>
/// Softens mouse-wheel scrolling with exponential smoothing toward a target offset.
/// </summary>
public static class SmoothScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(SmoothScroll));

    private static readonly AttachedProperty<State?> StateProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, State?>("State", typeof(SmoothScroll));

    static SmoothScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(AvaloniaObject obj) => obj.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(AvaloniaObject obj, bool value) => obj.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs e)
    {
        var existing = viewer.GetValue(StateProperty);
        existing?.Detach();
        viewer.SetValue(StateProperty, null);

        if (!e.GetNewValue<bool>()) return;

        var state = new State(viewer);
        viewer.SetValue(StateProperty, state);
        state.Attach();
    }

    private sealed class State
    {
        private readonly ScrollViewer _viewer;
        private DispatcherTimer? _timer;
        private double _targetY;
        private bool _attached;
        private bool _animating;

        // Smaller steps + soft exponential settle = less jitter than spring+velocity.
        private const double WheelPixelsPerNotch = 42;
        private const double TrackpadScale = 28;
        private const double Ease = 0.16;
        private const double SnapEpsilon = 0.4;

        public State(ScrollViewer viewer) => _viewer = viewer;

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            _viewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            _viewer.IsScrollInertiaEnabled = true;
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _viewer.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            StopTimer();
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            if (Math.Abs(e.Delta.Y) < 0.01 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
                return;

            var max = MaxOffset();
            if (max <= 0) return;

            e.Handled = true;

            var current = _viewer.Offset.Y;
            if (!_animating)
                _targetY = current;

            // Mouse notches are ±1; trackpads send smaller fractions — scale both gently.
            var step = Math.Abs(e.Delta.Y) >= 0.95
                ? WheelPixelsPerNotch * Math.Sign(e.Delta.Y)
                : e.Delta.Y * TrackpadScale;
            _targetY = Math.Clamp(_targetY - step, 0, max);

            EnsureTimer();
        }

        private double MaxOffset()
        {
            var extent = _viewer.Extent.Height;
            var viewport = _viewer.Viewport.Height;
            return Math.Max(0, extent - viewport);
        }

        private void EnsureTimer()
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromMilliseconds(1000.0 / 120.0)
                };
                _timer.Tick += OnTick;
            }

            _animating = true;
            if (!_timer.IsEnabled)
                _timer.Start();
        }

        private void StopTimer()
        {
            _animating = false;
            if (_timer is null) return;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var max = MaxOffset();
            _targetY = Math.Clamp(_targetY, 0, max);

            var current = _viewer.Offset.Y;
            var remaining = _targetY - current;

            if (Math.Abs(remaining) < SnapEpsilon)
            {
                _viewer.Offset = new Vector(_viewer.Offset.X, _targetY);
                StopTimer();
                return;
            }

            // Exponential ease — never overshoots, feels continuous.
            var next = current + remaining * Ease;
            if (Math.Abs(_targetY - next) < SnapEpsilon)
                next = _targetY;

            _viewer.Offset = new Vector(_viewer.Offset.X, Math.Clamp(next, 0, max));
        }
    }
}
