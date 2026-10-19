using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>A captured image kept on top of everything, e.g. a reference while typing something off it.</summary>
/// <remarks>
/// Drag moves it, the wheel zooms, double-click or Esc closes. Owned by the module: releasing it closes every
/// pin, since a module never keeps windows open without a tab.
/// </remarks>
internal sealed class PinWindow : Window
{
    private readonly Bitmap _bitmap;
    private readonly CapturedImage _image;
    private readonly Size _natural;
    private double _zoom = 1;

    private PinWindow(CapturedImage image, double scaling, double fit)
    {
        _image = image;
        _bitmap = Images.ToBitmap(image);
        _natural = new Size(image.Width / scaling, image.Height / scaling);
        _zoom = fit;

        WindowDecorations = WindowDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Background = Brushes.Black;
        Title = "Pinned screenshot";

        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            Child = new Image { Source = _bitmap, Stretch = Stretch.Fill },
        };

        var menu = new ContextMenu();
        menu.Items.Add(Item("Copy", () => ClipboardImage.Set(_image, null)));
        menu.Items.Add(Item("Actual size", () => Zoom(1)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close", Close));
        ContextMenu = menu;

        ToolTip.SetTip(this, "Drag to move · wheel zooms · double-click closes");
        Apply();
    }

    /// <summary>Opens a pin near the mouse pointer, at most two thirds of that display.</summary>
    public static PinWindow Open(CapturedImage image)
    {
        var (cx, cy) = ScreenCapture.Cursor();
        var monitors = Monitors.All();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(cx, cy)) ?? monitors.FirstOrDefault();

        var scaling = monitor?.Scaling ?? 1;
        var fit = 1.0;

        if (monitor is not null)
        {
            fit = Math.Min(1, Math.Min(
                monitor.Bounds.Width * 2.0 / 3 / image.Width,
                monitor.Bounds.Height * 2.0 / 3 / image.Height));
        }

        var window = new PinWindow(image, scaling, fit) { WindowStartupLocation = WindowStartupLocation.Manual };

        // Centered on the pointer, kept on the display.
        var width = (int)(image.Width * fit);
        var height = (int)(image.Height * fit);
        var x = cx - width / 2;
        var y = cy - height / 2;

        if (monitor is not null)
        {
            x = Math.Clamp(x, monitor.Bounds.X, Math.Max(monitor.Bounds.X, monitor.Bounds.X + monitor.Bounds.Width - width));
            y = Math.Clamp(y, monitor.Bounds.Y, Math.Max(monitor.Bounds.Y, monitor.Bounds.Y + monitor.Bounds.Height - height));
        }

        window.Position = new PixelPoint(x, y);
        window.Show();
        return window;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void Zoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.1, 4);
        Apply();
    }

    private void Apply()
    {
        Width = Math.Max(24, _natural.Width * _zoom);
        Height = Math.Max(24, _natural.Height * _zoom);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        BeginMoveDrag(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Zoom(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap.Dispose();
    }
}
