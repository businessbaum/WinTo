using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MultiTool.Modules.Video;

/// <summary>The timeline: where the video is, and which part is kept.</summary>
/// <remarks>
/// The kept part is highlighted between two handles; dragging a handle moves start or end, a click or drag
/// anywhere else moves the playhead. Times in seconds; the view model turns them into <see cref="TimeSpan"/>.
/// </remarks>
public sealed class TrimBar : Control
{
    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<TrimBar, double>(nameof(Duration));

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<TrimBar, double>(nameof(Position), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> StartProperty =
        AvaloniaProperty.Register<TrimBar, double>(nameof(Start), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> EndProperty =
        AvaloniaProperty.Register<TrimBar, double>(nameof(End), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private const double HandleWidth = 10;
    private const double Track = 8;

    private enum Grip { None, Start, End, Seek }

    private Grip _grip;

    static TrimBar()
    {
        AffectsRender<TrimBar>(DurationProperty, PositionProperty, StartProperty, EndProperty);
        FocusableProperty.OverrideDefaultValue<TrimBar>(true);
    }

    public TrimBar()
    {
        Height = 40;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public double Position
    {
        get => GetValue(PositionProperty);
        set => SetValue(PositionProperty, value);
    }

    public double Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    public double End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    private double Left => HandleWidth;

    private double Width2 => Math.Max(1, Bounds.Width - HandleWidth * 2);

    private double X(double seconds) => Left + (Duration > 0 ? seconds / Duration : 0) * Width2;

    private double Seconds(double x) => Math.Clamp((x - Left) / Width2, 0, 1) * Duration;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Duration <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var x = e.GetPosition(this).X;
        _grip = Math.Abs(x - X(Start)) <= HandleWidth ? Grip.Start
            : Math.Abs(x - X(End)) <= HandleWidth ? Grip.End
            : Grip.Seek;

        Drag(x);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_grip != Grip.None) Drag(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _grip = Grip.None;
        e.Pointer.Capture(null);
    }

    private void Drag(double x)
    {
        var seconds = Seconds(x);
        const double minimum = 0.1;

        switch (_grip)
        {
            case Grip.Start:
                Start = Math.Min(seconds, End - minimum);
                Position = Start;
                break;
            case Grip.End:
                End = Math.Max(seconds, Start + minimum);
                Position = End;
                break;
            case Grip.Seek:
                Position = seconds;
                break;
        }
    }

    public override void Render(DrawingContext context)
    {
        var mid = Bounds.Height / 2;
        var track = new Rect(Left, mid - Track / 2, Width2, Track);

        context.DrawRectangle(Brush("BgActiveBrush"), null, track, Track / 2, Track / 2);
        if (Duration <= 0) return;

        var kept = new Rect(X(Start), mid - Track / 2, Math.Max(0, X(End) - X(Start)), Track);
        context.DrawRectangle(Brush("AccentSoftBrush"), new Pen(Brush("AccentBrush"), 1), kept, 3, 3);

        // Handles: rounded bars at start and end.
        foreach (var at in new[] { X(Start), X(End) })
            context.DrawRectangle(Brush("AccentBrush"), null, new Rect(at - 3, mid - 12, 6, 24), 3, 3);

        // Playhead: a thin line with a dot.
        var head = X(Math.Clamp(Position, 0, Duration));
        var text = Brush("TextPrimaryBrush");
        context.DrawLine(new Pen(text, 2), new Point(head, 4), new Point(head, Bounds.Height - 4));
        context.DrawEllipse(text, null, new Point(head, 4), 4, 4);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
