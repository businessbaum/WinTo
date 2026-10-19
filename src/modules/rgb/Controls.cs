using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace MultiTool.Modules.Rgb;

/// <summary>A slider whose track is a brush: the hue rainbow, white to the color, dark to bright.</summary>
/// <remarks>
/// Drawn by hand rather than restyling Fluent's slider: its track is two repeat buttons around the thumb, and a
/// gradient across both doesn't survive their templates.
/// </remarks>
public sealed class ColorSlider : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ColorSlider, double>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<ColorSlider, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<ColorSlider, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<ColorSlider, IBrush?>(nameof(Track));

    private const double Thumb = 9;

    static ColorSlider()
    {
        AffectsRender<ColorSlider>(ValueProperty, MinimumProperty, MaximumProperty, TrackProperty);
        FocusableProperty.OverrideDefaultValue<ColorSlider>(true);
    }

    public ColorSlider()
    {
        Height = 24;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var middle = Bounds.Height / 2;
        var track = new Rect(Thumb, middle - 5, Math.Max(0, width - 2 * Thumb), 10);

        context.DrawRectangle(Track ?? Brushes.Gray, new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))), track, 5, 5);

        var span = Maximum - Minimum;
        var x = Thumb + (span <= 0 ? 0 : Math.Clamp((Value - Minimum) / span, 0, 1)) * track.Width;
        var center = new Point(x, middle);

        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, center, Thumb + 1.5, Thumb + 1.5);
        context.DrawEllipse(Brushes.White, IsFocused ? new Pen(Brushes.Black, 1.5) : null, center, Thumb, Thumb);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        e.Pointer.Capture(this);
        Focus();
        SetFrom(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured == this) SetFrom(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured == this) e.Pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = (Maximum - Minimum) / 100;
        var change = e.Key switch
        {
            Key.Left or Key.Down => -step,
            Key.Right or Key.Up => step,
            Key.PageDown => -step * 10,
            Key.PageUp => step * 10,
            Key.Home => Minimum - Value,
            Key.End => Maximum - Value,
            _ => 0,
        };

        if (change == 0) return;
        Value = Math.Clamp(Value + change, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }

    private void SetFrom(double x)
    {
        var width = Math.Max(1, Bounds.Width - 2 * Thumb);
        Value = Minimum + Math.Clamp((x - Thumb) / width, 0, 1) * (Maximum - Minimum);
    }
}

/// <summary>A strip of LEDs showing the chosen lighting live, also without a device connected.</summary>
/// <remarks>Animates only while visible and while the effect moves; a still color is drawn once.</remarks>
public sealed class EffectPreview : Control
{
    public static readonly StyledProperty<Lighting?> LightingProperty =
        AvaloniaProperty.Register<EffectPreview, Lighting?>(nameof(Lighting));

    private const int Leds = 28;

    private readonly RgbColor[] _colors = new RgbColor[Leds];
    private readonly float[] _positions = [.. Enumerable.Range(0, Leds).Select(i => i / (float)(Leds - 1))];
    private readonly DispatcherTimer _timer;
    private readonly DateTime _start = DateTime.UtcNow;
    private bool _attached;

    static EffectPreview() => AffectsRender<EffectPreview>(LightingProperty);

    public EffectPreview()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
    }

    public Lighting? Lighting
    {
        get => GetValue(LightingProperty);
        set => SetValue(LightingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LightingProperty || change.Property == IsVisibleProperty) UpdateTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
    }

    private void UpdateTimer()
    {
        var animate = Lighting?.Moves == true && IsEffectivelyVisible && _attached;
        if (animate) _timer.Start();
        else _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        var lighting = Lighting ?? new Lighting();
        Effects.Render(lighting, _positions, (DateTime.UtcNow - _start).TotalSeconds, _colors);

        var bounds = Bounds;
        var gap = 4.0;
        var width = (bounds.Width - gap * (Leds - 1)) / Leds;
        var height = Math.Min(bounds.Height - 16, 22);
        var top = (bounds.Height - height) / 2;

        for (var i = 0; i < Leds; i++)
        {
            var c = _colors[i];
            var color = Color.FromRgb(c.R, c.G, c.B);
            var x = i * (width + gap);
            var center = new Point(x + width / 2, bounds.Height / 2);

            // The glow around a lit LED; dark ones get none.
            var level = Math.Max(c.R, Math.Max(c.G, c.B)) / 255d;
            if (level > 0.02)
            {
                var glow = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb((byte)(120 * level), c.R, c.G, c.B), 0),
                        new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
                    },
                };
                context.DrawEllipse(glow, null, center, width * 1.4, bounds.Height / 2);
            }

            context.DrawRectangle(new SolidColorBrush(level > 0.02 ? color : Color.FromRgb(40, 42, 50)), null,
                new Rect(x, top, width, height), 4, 4);
        }
    }
}
