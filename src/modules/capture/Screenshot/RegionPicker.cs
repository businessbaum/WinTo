using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>A rectangle in screen pixels.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>Lets the user drag a region over a frozen image of the screen.</summary>
/// <remarks>
/// <para>
/// <b>Frozen, not live.</b> Captured before the picker appears; that image is shown and cropped. A menu
/// or tooltip that would close when the picker appears stays in the picture, like the Windows
/// Snipping Tool does it.
/// </para>
/// <para>
/// <b>One fullscreen window per display.</b> A single window across displays fails with different
/// scaling: Avalonia scales a window by the display it is on. A region spanning two displays isn't
/// possible; a click without dragging takes the whole display instead.
/// </para>
/// </remarks>
internal static class RegionPicker
{
    /// <param name="desktop">The image of the whole virtual desktop.</param>
    /// <param name="origin">Where its top left corner is on screen.</param>
    /// <param name="hint">The line shown before dragging; the default explains region and whole display.</param>
    /// <returns>The chosen region in screen pixels, or <c>null</c> if cancelled.</returns>
    public static Task<ScreenRect?> PickAsync(
        IReadOnlyList<Monitor> monitors, CapturedImage desktop, (int X, int Y) origin, string? hint = null)
    {
        var done = new TaskCompletionSource<ScreenRect?>();
        var windows = new List<Window>();
        var bitmaps = new List<Bitmap>();

        void Finish(ScreenRect? result)
        {
            if (!done.TrySetResult(result)) return;

            foreach (var window in windows) window.Close();
            foreach (var bitmap in bitmaps) bitmap.Dispose();
        }

        var cursor = ScreenCapture.Cursor();

        foreach (var monitor in monitors)
        {
            var part = desktop.Crop(monitor.Bounds.X - origin.X, monitor.Bounds.Y - origin.Y,
                monitor.Bounds.Width, monitor.Bounds.Height);
            if (part is null) continue;

            var bitmap = Images.ToBitmap(part);
            bitmaps.Add(bitmap);

            var surface = new SelectionSurface(bitmap, monitor.Bounds, hint);
            surface.Selected += rect => Finish(rect);
            surface.Cancelled += () => Finish(null);

            var window = new Window
            {
                WindowDecorations = WindowDecorations.None,
                Topmost = true,
                ShowInTaskbar = false,
                CanResize = false,
                Background = Brushes.Black,
                Content = surface,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(monitor.Bounds.X, monitor.Bounds.Y),

                // A starting size that fits the display; fullscreen takes the exact one.
                Width = Math.Max(100, monitor.Bounds.Width / monitor.Scaling),
                Height = Math.Max(100, monitor.Bounds.Height / monitor.Scaling),
            };

            window.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Finish(null);
            };

            // A window closed some other way (Alt+F4) cancels the whole picker.
            window.Closed += (_, _) => Finish(null);

            window.Opened += (_, _) =>
            {
                window.WindowState = WindowState.FullScreen;

                // Focus the display under the mouse pointer so Escape arrives where one is looking.
                if (monitor.Bounds.Contains(cursor.X, cursor.Y)) window.Activate();
            };

            windows.Add(window);
        }

        if (windows.Count == 0) return Task.FromResult<ScreenRect?>(null);

        foreach (var window in windows) window.Show();
        return done.Task;
    }
}

/// <summary>The frozen image of one display with the selection on it.</summary>
internal sealed class SelectionSurface : Control
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x11, 0x18));

    /// <summary>The selection frame in the app's accent color, which the user can change.</summary>
    private readonly IPen _edge;

    private readonly Bitmap _image;
    private readonly ScreenRect _screen;
    private Point? _start;
    private Point _current;

    private readonly string _hint;

    public SelectionSurface(Bitmap image, ScreenRect screen, string? hint = null)
    {
        _hint = hint ?? "Drag a region · click takes the whole display · Esc cancels";

        var accent = Application.Current?.TryGetResource("AccentBrush", null, out var value) == true
            && value is IBrush brush ? brush : new SolidColorBrush(Color.Parse("#7C5CFF"));
        _edge = new Pen(accent, 1.5);

        _image = image;
        _screen = screen;
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;
    }

    public event Action<ScreenRect>? Selected;

    public event Action? Cancelled;

    private Rect Selection => _start is { } start
        ? new Rect(
            new Point(Math.Min(start.X, _current.X), Math.Min(start.Y, _current.Y)),
            new Point(Math.Max(start.X, _current.X), Math.Max(start.Y, _current.Y)))
        : default;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            Cancelled?.Invoke();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _start = _current = point.Position;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_start is null) return;

        _current = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_start is null) return;

        e.Pointer.Capture(null);
        var selection = Selection;
        _start = null;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

        // A click without real dragging takes the whole display.
        if (selection.Width * scaling < 4 || selection.Height * scaling < 4)
        {
            Selected?.Invoke(_screen);
            return;
        }

        var x = _screen.X + (int)Math.Round(selection.X * scaling);
        var y = _screen.Y + (int)Math.Round(selection.Y * scaling);
        var right = _screen.X + (int)Math.Round(selection.Right * scaling);
        var bottom = _screen.Y + (int)Math.Round(selection.Bottom * scaling);

        Selected?.Invoke(new ScreenRect(x, y, right - x, bottom - y));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawImage(_image, bounds);

        var selection = Selection;

        if (selection.Width <= 0 || selection.Height <= 0)
        {
            context.FillRectangle(Dim, bounds);
            DrawLabel(context, _hint, new Point(bounds.Width / 2, 28), centered: true);
            return;
        }

        // Dim around it; the selection stays bright.
        context.FillRectangle(Dim, new Rect(0, 0, bounds.Width, selection.Top));
        context.FillRectangle(Dim, new Rect(0, selection.Bottom, bounds.Width, bounds.Height - selection.Bottom));
        context.FillRectangle(Dim, new Rect(0, selection.Top, selection.Left, selection.Height));
        context.FillRectangle(Dim, new Rect(selection.Right, selection.Top, bounds.Width - selection.Right, selection.Height));
        context.DrawRectangle(null, _edge, selection);

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var size = $"{(int)Math.Round(selection.Width * scaling)} × {(int)Math.Round(selection.Height * scaling)}";
        var labelY = selection.Top > 30 ? selection.Top - 26 : selection.Bottom + 6;
        DrawLabel(context, size, new Point(selection.Left, labelY), centered: false);
    }

    private static void DrawLabel(DrawingContext context, string text, Point at, bool centered)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.White);

        var x = centered ? at.X - formatted.Width / 2 - 10 : at.X;
        var box = new Rect(x, at.Y, formatted.Width + 20, formatted.Height + 8);

        context.DrawRectangle(LabelBackground, null, box, 6, 6);
        context.DrawText(formatted, new Point(box.X + 10, box.Y + 4));
    }
}
