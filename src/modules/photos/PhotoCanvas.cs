using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MultiTool.Modules.Photos;

/// <summary>Shows a picture: fitted, zoomed around the pointer, dragged around; optionally with a crop frame.</summary>
/// <remarks>
/// <para>
/// <b>Fit never enlarges.</b> A small picture is shown at its size, sharp, instead of blown up. The wheel zooms
/// around the pointer, double-click switches between fit and 100 %, dragging moves a zoomed picture.
/// </para>
/// <para>
/// <b>Cropping</b>: drag inside the frame to move it, near a corner or an edge to resize it, outside to draw a
/// new one. With an aspect ratio the frame keeps it. The frame is in picture pixels and survives zooming.
/// </para>
/// </remarks>
public sealed class PhotoCanvas : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<PhotoCanvas, Bitmap?>(nameof(Source));

    /// <summary>0 = fit; otherwise screen pixels per picture pixel.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PhotoCanvas, double>(nameof(Zoom), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<bool> CroppingProperty =
        AvaloniaProperty.Register<PhotoCanvas, bool>(nameof(Cropping));

    /// <summary>Width divided by height; 0 for a free frame.</summary>
    public static readonly StyledProperty<double> CropAspectProperty =
        AvaloniaProperty.Register<PhotoCanvas, double>(nameof(CropAspect));

    /// <summary>The frame in picture pixels; set by dragging, read when cropping is applied.</summary>
    public static readonly StyledProperty<Rect> CropRectProperty =
        AvaloniaProperty.Register<PhotoCanvas, Rect>(nameof(CropRect), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private static readonly IBrush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
    private static readonly IPen FramePen = new Pen(Brushes.White, 1.5);
    private static readonly IPen ThirdsPen = new Pen(new SolidColorBrush(Color.FromArgb(0x70, 255, 255, 255)), 1);
    private static readonly IBrush HandleBrush = Brushes.White;

    private Vector _pan;
    private Point? _dragFrom;
    private Vector _panFrom;
    private Rect _cropFrom;
    private CropGrip _grip;
    private ImageBrush? _checker;

    private enum CropGrip { None, Move, New, TopLeft, TopRight, BottomLeft, BottomRight, Left, Right, Top, Bottom }

    static PhotoCanvas()
    {
        AffectsRender<PhotoCanvas>(SourceProperty, ZoomProperty, CroppingProperty, CropRectProperty);
        ClipToBoundsProperty.OverrideDefaultValue<PhotoCanvas>(true);
        FocusableProperty.OverrideDefaultValue<PhotoCanvas>(true);
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public bool Cropping
    {
        get => GetValue(CroppingProperty);
        set => SetValue(CroppingProperty, value);
    }

    public double CropAspect
    {
        get => GetValue(CropAspectProperty);
        set => SetValue(CropAspectProperty, value);
    }

    public Rect CropRect
    {
        get => GetValue(CropRectProperty);
        set => SetValue(CropRectProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty)
        {
            _pan = default;

            // After a crop or a rotation the old frame no longer fits: a new one over the whole picture.
            if (Cropping && Source is { } changed)
                CropRect = Fitted(new Rect(0, 0, changed.PixelSize.Width, changed.PixelSize.Height));
        }
        else if (change.Property == CroppingProperty && Cropping && Source is { } source)
        {
            // Starts as the whole picture, or the largest frame of the chosen ratio.
            CropRect = Fitted(new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height));
        }
        else if (change.Property == CropAspectProperty && Cropping && Source is { } current)
        {
            CropRect = Fitted(new Rect(0, 0, current.PixelSize.Width, current.PixelSize.Height));
        }
    }

    /// <summary>The largest rectangle of the crop ratio inside <paramref name="area"/>, centered.</summary>
    private Rect Fitted(Rect area)
    {
        if (CropAspect <= 0) return area;

        var width = Math.Min(area.Width, area.Height * CropAspect);
        var height = width / CropAspect;
        return new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
    }

    // --------------------------------------------------------------- Geometry

    private double FitScale(Size picture) =>
        Math.Min(1, Math.Min(Bounds.Width / Math.Max(1, picture.Width), Bounds.Height / Math.Max(1, picture.Height)));

    private double Scale(Size picture) => Zoom > 0 ? Zoom : FitScale(picture);

    /// <summary>Where the picture lands on screen at the current zoom and pan.</summary>
    private Rect Placement(Size picture)
    {
        var scale = Scale(picture);
        var size = new Size(picture.Width * scale, picture.Height * scale);

        // Centered when smaller than the view; otherwise panned, never past an edge.
        var x = size.Width <= Bounds.Width ? (Bounds.Width - size.Width) / 2
            : Math.Clamp((Bounds.Width - size.Width) / 2 + _pan.X, Bounds.Width - size.Width, 0);
        var y = size.Height <= Bounds.Height ? (Bounds.Height - size.Height) / 2
            : Math.Clamp((Bounds.Height - size.Height) / 2 + _pan.Y, Bounds.Height - size.Height, 0);

        return new Rect(new Point(x, y), size);
    }

    private Point ToPicture(Point screen, Rect placement, Size picture) =>
        new((screen.X - placement.X) / placement.Width * picture.Width, (screen.Y - placement.Y) / placement.Height * picture.Height);

    private Rect ToScreen(Rect pictureRect, Rect placement, Size picture)
    {
        var sx = placement.Width / picture.Width;
        var sy = placement.Height / picture.Height;
        return new Rect(placement.X + pictureRect.X * sx, placement.Y + pictureRect.Y * sy, pictureRect.Width * sx, pictureRect.Height * sy);
    }

    // --------------------------------------------------------------- Input

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is not { } source) return;

        var picture = new Size(source.PixelSize.Width, source.PixelSize.Height);
        var placement = Placement(picture);
        var pointer = e.GetPosition(this);
        var anchor = ToPicture(pointer, placement, picture);

        var scale = Scale(picture);
        var next = Math.Clamp(scale * Math.Pow(1.18, e.Delta.Y), 0.02, 16);
        Zoom = next;

        // Keep the picture point under the pointer where it was.
        var size = new Size(picture.Width * next, picture.Height * next);
        var centered = new Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2);
        var wanted = new Point(pointer.X - anchor.X * next, pointer.Y - anchor.Y * next);
        _pan = new Vector(wanted.X - centered.X, wanted.Y - centered.Y);

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source is not { } source || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        Focus();
        var point = e.GetPosition(this);

        if (e.ClickCount == 2 && !Cropping)
        {
            // Fit ↔ 100 %, centered on the click.
            var picture = new Size(source.PixelSize.Width, source.PixelSize.Height);
            if (Zoom > 0)
            {
                Zoom = 0;
                _pan = default;
            }
            else
            {
                var anchor = ToPicture(point, Placement(picture), picture);
                Zoom = 1;
                var centered = new Point((Bounds.Width - picture.Width) / 2, (Bounds.Height - picture.Height) / 2);
                _pan = new Vector(point.X - anchor.X - centered.X, point.Y - anchor.Y - centered.Y);
            }

            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragFrom = point;
        _panFrom = _pan;
        _cropFrom = CropRect;
        _grip = Cropping ? GripAt(point, source) : CropGrip.None;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Source is not { } source) return;

        var point = e.GetPosition(this);
        var picture = new Size(source.PixelSize.Width, source.PixelSize.Height);

        if (_dragFrom is not { } from)
        {
            if (Cropping) Cursor = new Cursor(CursorFor(GripAt(point, source)));
            return;
        }

        if (!Cropping)
        {
            _pan = _panFrom + (point - from);
            InvalidateVisual();
            return;
        }

        var placement = Placement(picture);
        var a = ToPicture(from, placement, picture);
        var b = ToPicture(point, placement, picture);
        var d = b - a;
        var bounds = new Rect(picture);
        var r = _cropFrom;

        var next = _grip switch
        {
            CropGrip.Move => MoveWithin(r.Translate(d), bounds),
            CropGrip.New => new Rect(a, b).Normalize(),
            CropGrip.TopLeft => new Rect(r.TopLeft + d, r.BottomRight).Normalize(),
            CropGrip.TopRight => new Rect(new Point(r.Left, r.Top + d.Y), new Point(r.Right + d.X, r.Bottom)).Normalize(),
            CropGrip.BottomLeft => new Rect(new Point(r.Left + d.X, r.Top), new Point(r.Right, r.Bottom + d.Y)).Normalize(),
            CropGrip.BottomRight => new Rect(r.TopLeft, r.BottomRight + d).Normalize(),
            CropGrip.Left => new Rect(new Point(r.Left + d.X, r.Top), r.BottomRight).Normalize(),
            CropGrip.Right => new Rect(r.TopLeft, new Point(r.Right + d.X, r.Bottom)).Normalize(),
            CropGrip.Top => new Rect(new Point(r.Left, r.Top + d.Y), r.BottomRight).Normalize(),
            CropGrip.Bottom => new Rect(r.TopLeft, new Point(r.Right, r.Bottom + d.Y)).Normalize(),
            _ => r,
        };

        if (_grip != CropGrip.Move) next = Constrain(next.Intersect(bounds), _grip);
        if (next.Width >= 8 && next.Height >= 8) CropRect = next;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        _grip = CropGrip.None;
        e.Pointer.Capture(null);
    }

    /// <summary>Keeps the aspect ratio while resizing, anchored at the side not being dragged.</summary>
    private Rect Constrain(Rect rect, CropGrip grip)
    {
        if (CropAspect <= 0) return rect;

        var width = rect.Width;
        var height = width / CropAspect;
        if (grip is CropGrip.Top or CropGrip.Bottom)
        {
            height = rect.Height;
            width = height * CropAspect;
        }

        var x = grip is CropGrip.TopLeft or CropGrip.BottomLeft or CropGrip.Left ? rect.Right - width : rect.Left;
        var y = grip is CropGrip.TopLeft or CropGrip.TopRight or CropGrip.Top ? rect.Bottom - height : rect.Top;
        return new Rect(x, y, width, height);
    }

    private static Rect MoveWithin(Rect rect, Rect bounds) => new(
        Math.Clamp(rect.X, bounds.X, Math.Max(bounds.X, bounds.Right - rect.Width)),
        Math.Clamp(rect.Y, bounds.Y, Math.Max(bounds.Y, bounds.Bottom - rect.Height)),
        rect.Width, rect.Height);

    private CropGrip GripAt(Point point, Bitmap source)
    {
        var picture = new Size(source.PixelSize.Width, source.PixelSize.Height);
        var frame = ToScreen(CropRect, Placement(picture), picture);
        const double reach = 14;

        bool Near(double a, double b) => Math.Abs(a - b) <= reach;
        var inX = point.X >= frame.Left - reach && point.X <= frame.Right + reach;
        var inY = point.Y >= frame.Top - reach && point.Y <= frame.Bottom + reach;

        if (Near(point.X, frame.Left) && Near(point.Y, frame.Top)) return CropGrip.TopLeft;
        if (Near(point.X, frame.Right) && Near(point.Y, frame.Top)) return CropGrip.TopRight;
        if (Near(point.X, frame.Left) && Near(point.Y, frame.Bottom)) return CropGrip.BottomLeft;
        if (Near(point.X, frame.Right) && Near(point.Y, frame.Bottom)) return CropGrip.BottomRight;
        if (Near(point.X, frame.Left) && inY) return CropGrip.Left;
        if (Near(point.X, frame.Right) && inY) return CropGrip.Right;
        if (Near(point.Y, frame.Top) && inX) return CropGrip.Top;
        if (Near(point.Y, frame.Bottom) && inX) return CropGrip.Bottom;
        return frame.Contains(point) ? CropGrip.Move : CropGrip.New;
    }

    private static StandardCursorType CursorFor(CropGrip grip) => grip switch
    {
        CropGrip.TopLeft => StandardCursorType.TopLeftCorner,
        CropGrip.TopRight => StandardCursorType.TopRightCorner,
        CropGrip.BottomLeft => StandardCursorType.BottomLeftCorner,
        CropGrip.BottomRight => StandardCursorType.BottomRightCorner,
        CropGrip.Left or CropGrip.Right => StandardCursorType.SizeWestEast,
        CropGrip.Top or CropGrip.Bottom => StandardCursorType.SizeNorthSouth,
        CropGrip.Move => StandardCursorType.SizeAll,
        _ => StandardCursorType.Cross,
    };

    // --------------------------------------------------------------- Drawing

    public override void Render(DrawingContext context)
    {
        if (Source is not { } source) return;

        var picture = new Size(source.PixelSize.Width, source.PixelSize.Height);
        var placement = Placement(picture);

        // A checkerboard shows transparency as transparency, not as black.
        context.FillRectangle(Checker(), placement);

        var interpolation = Scale(picture) >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = interpolation }))
        {
            context.DrawImage(source, new Rect(picture), placement);
        }

        if (!Cropping) return;

        var frame = ToScreen(CropRect, placement, picture);

        // Everything outside the frame darkened, the frame, its thirds and its corners.
        context.FillRectangle(Dim, new Rect(placement.Left, placement.Top, placement.Width, Math.Max(0, frame.Top - placement.Top)));
        context.FillRectangle(Dim, new Rect(placement.Left, frame.Bottom, placement.Width, Math.Max(0, placement.Bottom - frame.Bottom)));
        context.FillRectangle(Dim, new Rect(placement.Left, frame.Top, Math.Max(0, frame.Left - placement.Left), frame.Height));
        context.FillRectangle(Dim, new Rect(frame.Right, frame.Top, Math.Max(0, placement.Right - frame.Right), frame.Height));

        for (var i = 1; i < 3; i++)
        {
            context.DrawLine(ThirdsPen, new Point(frame.Left + frame.Width * i / 3, frame.Top), new Point(frame.Left + frame.Width * i / 3, frame.Bottom));
            context.DrawLine(ThirdsPen, new Point(frame.Left, frame.Top + frame.Height * i / 3), new Point(frame.Right, frame.Top + frame.Height * i / 3));
        }

        context.DrawRectangle(null, FramePen, frame);

        const double handle = 8;
        foreach (var corner in new[] { frame.TopLeft, frame.TopRight, frame.BottomLeft, frame.BottomRight })
            context.FillRectangle(HandleBrush, new Rect(corner.X - handle / 2, corner.Y - handle / 2, handle, handle));
    }

    private ImageBrush Checker()
    {
        if (_checker is not null) return _checker;

        var bitmap = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
        using (var buffer = bitmap.Lock())
        {
            unsafe
            {
                for (var y = 0; y < 16; y++)
                {
                    var row = (uint*)((byte*)buffer.Address + y * buffer.RowBytes);
                    for (var x = 0; x < 16; x++) row[x] = (x < 8) ^ (y < 8) ? 0xFF2A2D38u : 0xFF353946u;
                }
            }
        }

        _checker = new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        };
        return _checker;
    }
}
