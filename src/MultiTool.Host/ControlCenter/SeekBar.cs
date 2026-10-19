using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MultiTool.Host.ControlCenter;

/// <summary>The thin progress line of "Now playing"; a click seeks where the app allows it.</summary>
/// <remarks>
/// Not a <c>Slider</c>: its value comes from the app every second, and a two-way slider would write that back as a
/// seek the moment it updates. Here only a click seeks, and nothing is written back.
/// </remarks>
public sealed class SeekBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<SeekBar, double>(nameof(Value));

    public static readonly StyledProperty<bool> CanSeekProperty = AvaloniaProperty.Register<SeekBar, bool>(nameof(CanSeek));

    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<SeekBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty = AvaloniaProperty.Register<SeekBar, IBrush?>(nameof(Track));

    static SeekBar()
    {
        AffectsRender<SeekBar>(ValueProperty, CanSeekProperty, FillProperty, TrackProperty, IsPointerOverProperty);
    }

    public SeekBar() => Height = 14;

    /// <summary>Asked to seek to a fraction of the track.</summary>
    public event Action<double>? Seek;

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool CanSeek
    {
        get => GetValue(CanSeekProperty);
        set => SetValue(CanSeekProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CanSeekProperty) Cursor = CanSeek ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
    }

    public override void Render(DrawingContext context)
    {
        // Thicker under the pointer when a click would seek, like macOS.
        var thickness = CanSeek && IsPointerOver ? 6 : 4;
        var top = (Bounds.Height - thickness) / 2;
        var width = Bounds.Width;
        var filled = width * Math.Clamp(Value, 0, 1);

        context.DrawRectangle(Track ?? Brushes.Gray, null, new Rect(0, top, width, thickness), thickness / 2d, thickness / 2d);
        if (filled > 0)
            context.DrawRectangle(Fill ?? Brushes.White, null, new Rect(0, top, Math.Max(filled, thickness), thickness), thickness / 2d, thickness / 2d);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!CanSeek || Bounds.Width <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Seek?.Invoke(e.GetPosition(this).X / Bounds.Width);
        e.Handled = true;
    }
}
