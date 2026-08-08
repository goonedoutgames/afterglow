using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Afterglow.Controls;

/// <summary>
/// Softens mouse-wheel scrolling by lerping Offset instead of jumping a full page per notch.
/// Attach with <c>ctrl:SmoothScroll.IsEnabled="True"</c> (also applied globally in theme).
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
        private double _velocity;
        private bool _attached;

        // Tuned for trackpad + mouse wheel: shorter steps, soft settle.
        private const double WheelPixelsPerNotch = 64;
        private const double Friction = 0.82;
        private const double SnapEpsilon = 0.35;

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
            // Only soften vertical page scrolls; leave horizontal strips alone.
            if (Math.Abs(e.Delta.Y) < 0.01 || Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y))
                return;

            var extent = _viewer.Extent.Height;
            var viewport = _viewer.Viewport.Height;
            var max = Math.Max(0, extent - viewport);
            if (max <= 0) return;

            e.Handled = true;

            var current = _viewer.Offset.Y;
            if (_timer is null || !_timer.IsEnabled)
                _targetY = current;

            // Delta.Y is typically ±1 per mouse notch; trackpads send fractions.
            _targetY = Math.Clamp(_targetY - e.Delta.Y * WheelPixelsPerNotch, 0, max);
            _velocity = (_targetY - current) * 0.35;

            EnsureTimer();
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

            if (!_timer.IsEnabled)
                _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer is null) return;
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            var extent = _viewer.Extent.Height;
            var viewport = _viewer.Viewport.Height;
            var max = Math.Max(0, extent - viewport);
            _targetY = Math.Clamp(_targetY, 0, max);

            var current = _viewer.Offset.Y;
            var remaining = _targetY - current;

            // Critically-damped-ish approach: blend velocity + spring pull.
            _velocity = _velocity * Friction + remaining * 0.22;
            var next = current + _velocity;

            if (Math.Abs(remaining) < SnapEpsilon && Math.Abs(_velocity) < SnapEpsilon)
            {
                _viewer.Offset = new Vector(_viewer.Offset.X, _targetY);
                _velocity = 0;
                _timer?.Stop();
                return;
            }

            // Overshoot clamp
            if ((_velocity > 0 && next > _targetY) || (_velocity < 0 && next < _targetY))
                next = _targetY;

            next = Math.Clamp(next, 0, max);
            _viewer.Offset = new Vector(_viewer.Offset.X, next);
        }
    }
}
