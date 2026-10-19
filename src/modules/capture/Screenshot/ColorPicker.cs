using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>Lets the user click any pixel of a frozen image of the screen and returns its color.</summary>
/// <remarks>
/// <para>
/// Built like <see cref="RegionPicker"/>: the screen is captured first and shown frozen, one fullscreen window per
/// display. Reading the pixel from the capture, not from the live screen, means the picker's own loupe never
/// ends up in the result.
/// </para>
/// <para>
/// <b>A loupe</b> follows the pointer: 11 × 11 pixels around it, enlarged, with the center pixel framed and the
/// hex value below. Single pixels are what a color picker is for; at 100 % they can't be aimed at.
/// </para>
/// </remarks>
internal static class ColorPicker
{
    /// <returns>The color, or <c>null</c> if cancelled.</returns>
    public static Task<Color?> PickAsync(IReadOnlyList<Monitor> monitors, CapturedImage desktop, (int X, int Y) origin)
    {
        var done = new TaskCompletionSource<Color?>();
        var windows = new List<Window>();
        var bitmaps = new List<Bitmap>();

        void Finish(Color? result)
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

            var surface = new LoupeSurface(bitmap, part);
            surface.Picked += color => Finish(color);
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
                Width = Math.Max(100, monitor.Bounds.Width / monitor.Scaling),
                Height = Math.Max(100, monitor.Bounds.Height / monitor.Scaling),
            };

            window.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Finish(null);
            };
            window.Closed += (_, _) => Finish(null);
            window.Opened += (_, _) =>
            {
                window.WindowState = WindowState.FullScreen;
                if (monitor.Bounds.Contains(cursor.X, cursor.Y)) window.Activate();
            };

            windows.Add(window);
        }

        if (windows.Count == 0) return Task.FromResult<Color?>(null);

        foreach (var window in windows) window.Show();
        return done.Task;
    }
}

/// <summary>The frozen image of one display with the loupe on it.</summary>
internal sealed class LoupeSurface : Control
{
    private const int Radius = 5;
    private const double Zoom = 12;

    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(0xE0, 0x10, 0x11, 0x18));
    private static readonly IPen Frame = new Pen(Brushes.White, 2);
    private static readonly IPen Inner = new Pen(Brushes.Black, 1);

    private readonly Bitmap _image;
    private readonly CapturedImage _pixels;
    private Point? _pointer;

    public LoupeSurface(Bitmap image, CapturedImage pixels)
    {
        _image = image;
        _pixels = pixels;
        Cursor = new Cursor(StandardCursorType.Cross);
        Focusable = true;
    }

    public event Action<Color>? Picked;

    public event Action? Cancelled;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _pointer = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            Cancelled?.Invoke();
            return;
        }

        if (point.Properties.IsLeftButtonPressed && PixelAt(point.Position) is var (x, y))
            Picked?.Invoke(ColorAt(x, y));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawImage(_image, bounds);

        if (_pointer is not { } pointer || PixelAt(pointer) is not var (px, py))
        {
            DrawLabel(context, "Click a pixel · Esc cancels", new Point(bounds.Width / 2, 28), centered: true);
            return;
        }

        var side = (Radius * 2 + 1) * Zoom;

        // Next to the pointer, flipped near the edges so it stays on screen.
        var left = pointer.X + 24 + side > bounds.Width ? pointer.X - 24 - side : pointer.X + 24;
        var top = pointer.Y + 24 + side + 36 > bounds.Height ? pointer.Y - 24 - side - 36 : pointer.Y + 24;

        for (var dy = -Radius; dy <= Radius; dy++)
        {
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                var x = Math.Clamp(px + dx, 0, _pixels.Width - 1);
                var y = Math.Clamp(py + dy, 0, _pixels.Height - 1);
                var cell = new Rect(left + (dx + Radius) * Zoom, top + (dy + Radius) * Zoom, Zoom, Zoom);
                context.FillRectangle(new SolidColorBrush(ColorAt(x, y)), cell);
            }
        }

        var loupe = new Rect(left, top, side, side);
        context.DrawRectangle(null, Frame, loupe);

        var center = new Rect(left + Radius * Zoom, top + Radius * Zoom, Zoom, Zoom);
        context.DrawRectangle(null, Inner, center.Inflate(1));
        context.DrawRectangle(null, Frame, center);

        DrawLabel(context, Hex(ColorAt(px, py)), new Point(left, top + side + 8), centered: false);
    }

    /// <summary>The image pixel under a point of this control.</summary>
    private (int X, int Y)? PixelAt(Point point)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return null;

        var x = (int)(point.X / Bounds.Width * _pixels.Width);
        var y = (int)(point.Y / Bounds.Height * _pixels.Height);
        return x >= 0 && y >= 0 && x < _pixels.Width && y < _pixels.Height ? (x, y) : null;
    }

    private Color ColorAt(int x, int y)
    {
        var i = (y * _pixels.Width + x) * 4;
        return Color.FromRgb(_pixels.Pixels[i + 2], _pixels.Pixels[i + 1], _pixels.Pixels[i]);
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

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
