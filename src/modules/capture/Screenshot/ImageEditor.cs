using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MultiTool.Modules.Capture.Screenshot;

public enum EditTool
{
    Arrow,
    Box,
    Pen,
    Marker,
    Text,
    Pixelate,
    Crop,
}

/// <summary>One step of an edit, in image pixels. Two points for shapes, many for pen and marker, one for text.</summary>
internal sealed record EditStep(EditTool Tool, Color Color, IReadOnlyList<Point> Points)
{
    public Rect Area => new Rect(Points[0], Points[^1]).Normalize();

    /// <summary>What a text step writes.</summary>
    public string Text { get; init; } = "";
}

/// <summary>A pen color in the editor's bar.</summary>
public sealed partial class EditorSwatch(string name, Color color) : ObservableObject
{
    public string Name { get; } = name;
    public Color Color { get; } = color;
    public IBrush Brush { get; } = new SolidColorBrush(color);

    [ObservableProperty] public partial bool IsActive { get; set; }
}

/// <summary>Arrows, boxes, pen, marker, text, pixelation and cropping over a captured image.</summary>
/// <remarks>
/// <para>
/// <b>Steps, not snapshots.</b> Every step is kept and the result is drawn from the original each time. Undo
/// drops the last step and costs no copy of a 30 MB image.
/// </para>
/// <para>
/// <b>Pixelation goes into the base image</b>, before the shapes, whatever the order: it hides what was on the
/// screen, not one's own arrows. The base is rebuilt only when a pixelation is added or undone.
/// </para>
/// <para>
/// <b>Cropping changes the view.</b> Shapes stay in full image coordinates, so undoing a crop brings back
/// everything around it.
/// </para>
/// </remarks>
public sealed partial class ImageEditor : ObservableObject, IDisposable
{
    private readonly List<EditStep> _steps = [];
    private readonly CapturedImage _source;
    private WriteableBitmap? _base;

    public ImageEditor(CapturedImage source)
    {
        _source = source;
        _base = Images.ToBitmap(source);

        Swatches =
        [
            new EditorSwatch("Red", Color.FromRgb(0xE5, 0x48, 0x4D)),
            new EditorSwatch("Yellow", Color.FromRgb(0xFF, 0xC5, 0x3D)),
            new EditorSwatch("Green", Color.FromRgb(0x30, 0xA4, 0x6C)),
            new EditorSwatch("Blue", Color.FromRgb(0x3E, 0x8B, 0xFF)),
            new EditorSwatch("White", Colors.White),
            new EditorSwatch("Black", Color.FromRgb(0x11, 0x11, 0x11)),
        ];

        Swatches[0].IsActive = true;
        Color = Swatches[0].Color;
        Stroke = Math.Max(3, Math.Min(source.Width, source.Height) / 260.0);
    }

    /// <summary>Raised when the picture changed; the surface redraws.</summary>
    public event Action? Changed;

    public ObservableCollection<EditorSwatch> Swatches { get; }

    [ObservableProperty] public partial EditTool Tool { get; set; } = EditTool.Arrow;
    [ObservableProperty] public partial bool CanUndo { get; set; }

    public Color Color { get; private set; }

    /// <summary>Line width in image pixels; thicker on larger images so it reads the same after scaling.</summary>
    public double Stroke { get; }

    /// <summary>Text height in image pixels, from the same rule.</summary>
    public double TextSize => Math.Max(16, Stroke * 7);

    public bool IsArrow => Tool == EditTool.Arrow;
    public bool IsBox => Tool == EditTool.Box;
    public bool IsPen => Tool == EditTool.Pen;
    public bool IsMarker => Tool == EditTool.Marker;
    public bool IsText => Tool == EditTool.Text;
    public bool IsPixelate => Tool == EditTool.Pixelate;
    public bool IsCrop => Tool == EditTool.Crop;

    public bool HasChanges => _steps.Count > 0;

    internal IReadOnlyList<EditStep> Steps => _steps;

    internal Bitmap? Base => _base;

    /// <summary>The visible part of the image: the last crop, or all of it.</summary>
    public Rect View => _steps.LastOrDefault(s => s.Tool == EditTool.Crop)?.Area.Intersect(Full) ?? Full;

    /// <summary>Size of the result in pixels: the crop, or the whole image.</summary>
    public string SizeText => $"{(int)View.Width} × {(int)View.Height}";

    private Rect Full => new(0, 0, _source.Width, _source.Height);

    partial void OnToolChanged(EditTool value)
    {
        OnPropertyChanged(nameof(IsArrow));
        OnPropertyChanged(nameof(IsBox));
        OnPropertyChanged(nameof(IsPen));
        OnPropertyChanged(nameof(IsMarker));
        OnPropertyChanged(nameof(IsText));
        OnPropertyChanged(nameof(IsPixelate));
        OnPropertyChanged(nameof(IsCrop));
    }

    [RelayCommand]
    private void SelectTool(string name)
    {
        if (Enum.TryParse<EditTool>(name, out var tool)) Tool = tool;
    }

    [RelayCommand]
    private void SelectColor(EditorSwatch swatch)
    {
        foreach (var item in Swatches) item.IsActive = item == swatch;
        Color = swatch.Color;
    }

    [RelayCommand]
    public void Undo()
    {
        if (_steps.Count == 0) return;

        var last = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        if (last.Tool == EditTool.Pixelate) RebuildBase();

        Notify();
    }

    /// <summary>Back to the image as captured.</summary>
    [RelayCommand]
    private void Reset()
    {
        if (_steps.Count == 0) return;

        var hadPixelation = _steps.Any(s => s.Tool == EditTool.Pixelate);
        _steps.Clear();
        if (hadPixelation) RebuildBase();

        Notify();
    }

    internal void Add(EditStep step)
    {
        _steps.Add(step);
        if (step.Tool == EditTool.Pixelate) RebuildBase();

        Notify();
    }

    private void Notify()
    {
        CanUndo = _steps.Count > 0;
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(SizeText));
        Changed?.Invoke();
    }

    /// <summary>The finished image as PNG. UI thread.</summary>
    public byte[] RenderPng()
    {
        var view = View;
        var size = new PixelSize(Math.Max(1, (int)view.Width), Math.Max(1, (int)view.Height));

        using var target = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var context = target.CreateDrawingContext())
        {
            if (_base is not null) context.DrawImage(_base, view, new Rect(0, 0, size.Width, size.Height));

            using (context.PushTransform(Matrix.CreateTranslation(-view.X, -view.Y)))
            {
                foreach (var step in _steps) Draw(context, step, Stroke, TextSize);
            }
        }

        using var stream = new MemoryStream();
        target.Save(stream, new PngBitmapEncoderOptions());
        return stream.ToArray();
    }

    /// <summary>The finished image as pixels, for the clipboard, pinning and text recognition.</summary>
    public CapturedImage Render()
    {
        if (!HasChanges) return _source;

        using var stream = new MemoryStream(RenderPng());
        using var bitmap = new Bitmap(stream);
        return Images.FromBitmap(bitmap);
    }

    /// <summary>One shape. Pixelation and crop are not shapes and draw nothing here.</summary>
    internal static void Draw(DrawingContext context, EditStep step, double stroke, double textSize)
    {
        var brush = new SolidColorBrush(step.Color);

        switch (step.Tool)
        {
            case EditTool.Box:
                context.DrawRectangle(null, new Pen(brush, stroke, lineJoin: PenLineJoin.Round), step.Area, stroke, stroke);
                break;

            case EditTool.Arrow:
                DrawArrow(context, brush, step.Points[0], step.Points[^1], stroke);
                break;

            case EditTool.Pen when step.Points.Count > 1:
                DrawLine(context, step.Points, new Pen(brush, stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
                break;

            case EditTool.Marker when step.Points.Count > 1:
                var marker = new SolidColorBrush(Color.FromArgb(0x66, step.Color.R, step.Color.G, step.Color.B));
                DrawLine(context, step.Points, new Pen(marker, stroke * 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
                break;

            case EditTool.Text when step.Text.Length > 0:
                DrawText(context, step, textSize);
                break;
        }
    }

    private static void DrawLine(DrawingContext context, IReadOnlyList<Point> points, IPen pen)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(points[0], false);
            for (var i = 1; i < points.Count; i++) g.LineTo(points[i]);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Text in the pen color with a thin contrasting outline, readable on any background.</summary>
    private static void DrawText(DrawingContext context, EditStep step, double size)
    {
        var text = new FormattedText(step.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size, null);

        if (text.BuildGeometry(step.Points[0]) is not { } geometry) return;

        var light = step.Color.R * 0.299 + step.Color.G * 0.587 + step.Color.B * 0.114 > 140;
        var outline = new Pen(new SolidColorBrush(light ? Color.FromArgb(0xCC, 0, 0, 0) : Color.FromArgb(0xCC, 255, 255, 255)),
            Math.Max(1.5, size / 12), lineJoin: PenLineJoin.Round);

        context.DrawGeometry(null, outline, geometry);
        context.DrawGeometry(new SolidColorBrush(step.Color), null, geometry);
    }

    private static void DrawArrow(DrawingContext context, IBrush brush, Point from, Point to, double stroke)
    {
        var delta = to - from;
        var length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (length < 1) return;

        var unit = new Vector(delta.X / length, delta.Y / length);
        var normal = new Vector(-unit.Y, unit.X);
        var head = Math.Min(stroke * 4.5, length * 0.6);

        // The line ends inside the head, or its round cap pokes out at the tip.
        var neck = to - unit * head;
        context.DrawLine(new Pen(brush, stroke, lineCap: PenLineCap.Round), from, neck + unit * (head * 0.3));

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(to, true);
            g.LineTo(neck + normal * (head * 0.6));
            g.LineTo(neck - normal * (head * 0.6));
            g.EndFigure(true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    /// <summary>The original with every pixelation applied, as blocks of their average color.</summary>
    private void RebuildBase()
    {
        var pixels = (byte[])_source.Pixels.Clone();
        var width = _source.Width;

        foreach (var step in _steps.Where(s => s.Tool == EditTool.Pixelate))
        {
            var area = step.Area.Intersect(Full);
            int left = (int)area.X, top = (int)area.Y, right = (int)area.Right, bottom = (int)area.Bottom;

            // Coarse enough that text below can't be read, even in a small box.
            var block = Math.Clamp((int)(Math.Min(area.Width, area.Height) / 6), 8, 24);

            for (var by = top; by < bottom; by += block)
            {
                for (var bx = left; bx < right; bx += block)
                {
                    var ex = Math.Min(bx + block, right);
                    var ey = Math.Min(by + block, bottom);
                    long b = 0, g = 0, r = 0, n = 0;

                    for (var y = by; y < ey; y++)
                    {
                        for (var x = bx; x < ex; x++)
                        {
                            var i = (y * width + x) * 4;
                            b += pixels[i];
                            g += pixels[i + 1];
                            r += pixels[i + 2];
                            n++;
                        }
                    }

                    if (n == 0) continue;

                    for (var y = by; y < ey; y++)
                    {
                        for (var x = bx; x < ex; x++)
                        {
                            var i = (y * width + x) * 4;
                            pixels[i] = (byte)(b / n);
                            pixels[i + 1] = (byte)(g / n);
                            pixels[i + 2] = (byte)(r / n);
                        }
                    }
                }
            }
        }

        var old = _base;
        _base = Images.ToBitmap(new CapturedImage(pixels, width, _source.Height) { Premultiplied = _source.Premultiplied });
        old?.Dispose();
    }

    public void Dispose()
    {
        _base?.Dispose();
        _base = null;
    }
}

/// <summary>Shows an <see cref="ImageEditor"/> and turns drags and clicks into its steps.</summary>
/// <remarks>
/// A panel, not a plain control: the text tool places a text box on the picture while typing. The picture is
/// drawn by an inner control filling the panel, since <c>Panel.Render</c> is sealed.
/// </remarks>
public sealed class EditorSurface : Panel
{
    private readonly Painter _painter;

    public static readonly StyledProperty<ImageEditor?> EditorProperty =
        AvaloniaProperty.Register<EditorSurface, ImageEditor?>(nameof(Editor));

    private static readonly IPen Outline = new Pen(Brushes.White, 1, new DashStyle([4, 4], 0));
    private static readonly IBrush Shade = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0));

    private List<Point>? _drag;
    private TextBox? _typing;
    private Point _typingAt;

    public EditorSurface()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
        ClipToBounds = true;
        Background = Brushes.Transparent;

        _painter = new Painter(this);
        Children.Add(_painter);
    }

    private void Redraw() => _painter.InvalidateVisual();

    /// <summary>Draws the picture and the steps for its panel.</summary>
    private sealed class Painter(EditorSurface owner) : Control
    {
        public override void Render(DrawingContext context) => owner.Paint(context);
    }

    public ImageEditor? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;

        if (change.OldValue is ImageEditor old) old.Changed -= Redraw;
        if (change.NewValue is ImageEditor editor) editor.Changed += Redraw;
        _drag = null;
        EndTyping(commit: false);
        Redraw();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _drag = null;
        EndTyping(commit: true);
    }

    /// <summary>Scale and offset from image pixels to this control, fitted and centered.</summary>
    private (double Scale, Point Offset, Rect View)? Layout()
    {
        if (Editor is not { } editor) return null;

        var view = editor.View;
        if (view.Width < 1 || view.Height < 1 || Bounds.Width < 1 || Bounds.Height < 1) return null;

        // Never larger than 1:1: a small capture stays sharp instead of blown up.
        var scale = Math.Min(1, Math.Min(Bounds.Width / view.Width, Bounds.Height / view.Height));
        var offset = new Point((Bounds.Width - view.Width * scale) / 2, (Bounds.Height - view.Height * scale) / 2);
        return (scale, offset, view);
    }

    private static Point ToImage(Point point, (double Scale, Point Offset, Rect View) layout)
    {
        var x = (point.X - layout.Offset.X) / layout.Scale + layout.View.X;
        var y = (point.Y - layout.Offset.Y) / layout.Scale + layout.View.Y;
        return new Point(Math.Clamp(x, layout.View.X, layout.View.Right), Math.Clamp(y, layout.View.Y, layout.View.Bottom));
    }

    private static Point ToScreen(Point point, (double Scale, Point Offset, Rect View) layout) =>
        new((point.X - layout.View.X) * layout.Scale + layout.Offset.X, (point.Y - layout.View.Y) * layout.Scale + layout.Offset.Y);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Editor is not { } editor || Layout() is not { } layout) return;

        // A click elsewhere ends the text being typed.
        if (_typing is not null && !ReferenceEquals(e.Source, _typing))
        {
            EndTyping(commit: true);
            e.Handled = true;
            return;
        }

        if (editor.Tool == EditTool.Text)
        {
            StartTyping(ToImage(e.GetPosition(this), layout), layout, editor);
            e.Handled = true;
            return;
        }

        _drag = [ToImage(e.GetPosition(this), layout)];
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is null || Editor is not { } editor || Layout() is not { } layout) return;

        var point = ToImage(e.GetPosition(this), layout);
        if (editor.Tool is EditTool.Marker or EditTool.Pen) _drag.Add(point);
        else if (_drag.Count == 1) _drag.Add(point);
        else _drag[^1] = point;

        Redraw();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } drag) return;

        _drag = null;
        e.Pointer.Capture(null);

        if (Editor is not { } editor || drag.Count < 2) return;

        var step = new EditStep(editor.Tool, editor.Color, drag);
        var tooSmall = editor.Tool switch
        {
            EditTool.Arrow => Distance(drag[0], drag[^1]) < 6,
            EditTool.Marker or EditTool.Pen => false,
            _ => step.Area.Width < 4 || step.Area.Height < 4,
        };

        if (!tooSmall) editor.Add(step);
        else Redraw();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = null;
        Redraw();
    }

    // --------------------------------------------------------------- Text

    private void StartTyping(Point at, (double Scale, Point Offset, Rect View) layout, ImageEditor editor)
    {
        EndTyping(commit: true);

        _typingAt = at;
        _typing = new TextBox
        {
            FontSize = Math.Max(10, editor.TextSize * layout.Scale),
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(editor.Color),
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            MinWidth = 60,
            MinHeight = 0,
            Padding = new Thickness(2, 0),
            AcceptsReturn = false,
            PlaceholderText = "Text",
        };

        _typing.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { EndTyping(commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndTyping(commit: false); e.Handled = true; }
        };
        _typing.LostFocus += (_, _) => EndTyping(commit: true);

        Children.Add(_typing);
        InvalidateArrange();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _typing?.Focus());
    }

    private void EndTyping(bool commit)
    {
        if (_typing is not { } box) return;

        _typing = null;
        Children.Remove(box);

        if (commit && Editor is { } editor && box.Text is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
        {
            // The box's top left is where the text's top sits.
            editor.Add(new EditStep(EditTool.Text, ((SolidColorBrush)box.Foreground!).Color, [_typingAt]) { Text = text });
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _painter.Arrange(new Rect(finalSize));

        if (_typing is { } box && Layout() is { } layout)
        {
            var at = ToScreen(_typingAt, layout);
            box.Arrange(new Rect(at, box.DesiredSize));
        }

        return finalSize;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(Size.Infinity);
        return default;
    }

    // --------------------------------------------------------------- Drawing

    private void Paint(DrawingContext context)
    {
        if (Editor is not { } editor || editor.Base is not { } image || Layout() is not { } layout) return;

        var (scale, offset, view) = layout;
        context.DrawImage(image, view, new Rect(offset, new Size(view.Width * scale, view.Height * scale)));

        var transform = Matrix.CreateTranslation(-view.X, -view.Y)
                        * Matrix.CreateScale(scale, scale)
                        * Matrix.CreateTranslation(offset.X, offset.Y);

        using (context.PushTransform(transform))
        {
            foreach (var step in editor.Steps) ImageEditor.Draw(context, step, editor.Stroke, editor.TextSize);

            if (_drag is { Count: > 1 } drag)
                ImageEditor.Draw(context, new EditStep(editor.Tool, editor.Color, drag), editor.Stroke, editor.TextSize);
        }

        // Pixelation and crop show their area while dragging; they have no shape of their own.
        if (_drag is { Count: > 1 } area && editor.Tool is EditTool.Pixelate or EditTool.Crop)
        {
            var rect = new Rect(area[0], area[^1]).Normalize().TransformToAABB(transform);
            if (editor.Tool == EditTool.Pixelate) context.FillRectangle(Shade, rect);
            context.DrawRectangle(null, Outline, rect);
        }
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
