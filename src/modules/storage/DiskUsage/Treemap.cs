using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace MultiTool.Modules.Storage.DiskUsage;

public enum ItemKind
{
    Folder,
    File,

    /// <summary>The files of a folder that weren't kept individually.</summary>
    Rest,

    /// <summary>Free space, only at the top level of a drive.</summary>
    Free,
}

/// <summary>An area of the treemap.</summary>
/// <param name="Folder">The folder behind the area; for files their parent folder.</param>
/// <param name="File">Only for <see cref="ItemKind.File"/>.</param>
public sealed record TreemapItem(string Name, long Size, ItemKind Kind, DirNode Folder, FileLeaf? File = null)
{
    public string Category => Kind switch
    {
        ItemKind.Folder => "Folder",
        ItemKind.Rest => "other files",
        ItemKind.Free => "free",
        _ => FileCategory.Of(Name),
    };

    /// <summary>The full path; empty for rest and free, which aren't single things.</summary>
    public string Path => Kind switch
    {
        ItemKind.Folder => Folder.FullPath,
        ItemKind.File => System.IO.Path.Combine(Folder.FullPath, Name),
        _ => "",
    };

    /// <summary>The areas a folder consists of, by size.</summary>
    public static List<TreemapItem> Of(DirNode node)
    {
        var items = new List<TreemapItem>(node.Children.Count + node.LargestFiles.Count + 1);

        foreach (var child in node.Children)
            if (child.Size > 0) items.Add(new TreemapItem(child.Name, child.Size, ItemKind.Folder, child));

        foreach (var file in node.LargestFiles)
            if (file.Size > 0) items.Add(new TreemapItem(file.Name, file.Size, ItemKind.File, node, file));

        if (node.OtherSize > 0)
            items.Add(new TreemapItem($"{node.OtherCount} smaller files", node.OtherSize, ItemKind.Rest, node));

        items.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        return items;
    }
}

/// <summary>Rough kind of a file by extension. Decides the color.</summary>
internal static class FileCategory
{
    private static readonly Dictionary<string, string> ByExtension = Build();

    public static string Of(string name) =>
        ByExtension.TryGetValue(System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant(), out var category)
            ? category
            : "File";

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string category, params string[] extensions)
        {
            foreach (var e in extensions) map[e] = category;
        }

        Add("Video", "mp4", "mkv", "avi", "mov", "webm", "wmv", "m4v", "ts", "flv");
        Add("Audio", "mp3", "wav", "flac", "ogg", "m4a", "aac", "opus", "wma");
        Add("Image", "png", "jpg", "jpeg", "gif", "webp", "bmp", "tif", "tiff", "heic", "psd", "raw", "cr2", "nef", "dng");
        Add("Archive", "zip", "7z", "rar", "iso", "vhd", "vhdx", "tar", "gz", "cab", "msi", "img", "wim");
        Add("Program", "exe", "dll", "sys", "pak", "vpk", "bundle", "assets", "bin", "dat", "ucas", "utoc", "forge", "upk");
        Add("Document", "pdf", "docx", "doc", "xlsx", "xls", "pptx", "txt", "md", "odt");
        return map;
    }
}

/// <summary>The treemap: larger entries take more area, labels carry the exact size.</summary>
/// <remarks>
/// <para>
/// <b>Areas are compressed, not proportional</b> (<see cref="Weight"/>). In true proportion one 440 GB
/// games folder next to nineteen folders of a few GB left those as slivers a few pixels wide, without
/// a label or a hit area. The order stays and the labels show the real sizes.
/// </para>
/// <para>
/// <b>Squarified (Bruls, Huizing and van Wijk).</b> Alternating horizontal and vertical strips produce
/// long thin rectangles that can't be hit or labeled; this lays out rows that keep sides close to
/// equal.
/// </para>
/// <para>
/// <b>Two levels:</b> each folder area shows its content, so it's visible at a glance whether a large
/// folder is one big file or ten thousand small ones. A click hits the innermost area, a double
/// click opens a folder.
/// </para>
/// <para>
/// Drawn in <see cref="Render"/>, not built from controls: a folder with hundreds of entries would
/// be as many controls, rebuilt on every resize.
/// </para>
/// </remarks>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<TreemapItem>?> ItemsProperty =
        AvaloniaProperty.Register<TreemapControl, IReadOnlyList<TreemapItem>?>(nameof(Items));

    public static readonly StyledProperty<TreemapItem?> SelectedItemProperty =
        AvaloniaProperty.Register<TreemapControl, TreemapItem?>(nameof(SelectedItem),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Areas smaller than this are not split into a second level.</summary>
    private const double NestMinimum = 48;

    private static readonly Dictionary<string, Color> Colors = new()
    {
        ["Folder"] = Color.Parse("#355C8F"),
        ["Video"] = Color.Parse("#8E3C6C"),
        ["Audio"] = Color.Parse("#8E6A36"),
        ["Image"] = Color.Parse("#2F8A69"),
        ["Archive"] = Color.Parse("#6A4CA0"),
        ["Program"] = Color.Parse("#2E7A8E"),
        ["Document"] = Color.Parse("#7A8A45"),
        ["File"] = Color.Parse("#596070"),
        ["other files"] = Color.Parse("#3A3F4B"),
        ["free"] = Color.Parse("#22252D"),
    };

    private static readonly IPen Gap = new Pen(new SolidColorBrush(Color.Parse("#14161C")), 1);
    private static readonly IPen SelectionPen = new Pen(Brushes.White, 2);
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush LabelShade = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));

    /// <summary>A computed area with everything needed to draw it.</summary>
    private sealed record Laid(Rect Rect, TreemapItem Item, int Depth, IBrush Fill, FormattedText? Label);

    /// <summary>Drawn areas, inner ones after their folder, so a reverse search hits the smallest.</summary>
    /// <remarks>
    /// Cached until entries or size change; otherwise every mouse move recomputed both levels and
    /// created a brush and text per area.
    /// </remarks>
    private readonly List<Laid> _laid = [];

    private IReadOnlyList<TreemapItem>? _laidFor;
    private Size _laidSize;

    private static readonly Dictionary<(string, int), IBrush> BrushCache = [];

    private TreemapItem? _hovered;

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(ItemsProperty, SelectedItemProperty);
        FocusableProperty.OverrideDefaultValue<TreemapControl>(true);
    }

    public TreemapControl() => ClipToBounds = true;

    public IReadOnlyList<TreemapItem>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public TreemapItem? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    /// <summary>Under the mouse pointer, or <c>null</c>.</summary>
    public event Action<TreemapItem?>? Hovered;

    /// <summary>Double click on a folder.</summary>
    public event Action<TreemapItem>? Opened;

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (!ReferenceEquals(_laidFor, Items) || _laidSize != size) Relayout(size);

        foreach (var laid in _laid)
        {
            context.FillRectangle(laid.Fill, laid.Rect);
            context.DrawRectangle(null, Gap, laid.Rect);

            if (laid.Label is { } label)
            {
                var strip = new Rect(laid.Rect.X, laid.Rect.Y, laid.Rect.Width, Math.Min(laid.Rect.Height, label.Height + 4));
                context.FillRectangle(LabelShade, strip);
                context.DrawText(label, new Point(laid.Rect.X + 4, laid.Rect.Y + 2));
            }
        }

        foreach (var laid in _laid)
        {
            if (Same(laid.Item, _hovered)) context.DrawRectangle(null, HoverPen, laid.Rect.Deflate(0.5));
            if (Same(laid.Item, SelectedItem)) context.DrawRectangle(null, SelectionPen, laid.Rect.Deflate(1));
        }
    }

    private void Relayout(Size size)
    {
        _laid.Clear();
        _laidFor = Items;
        _laidSize = size;

        var bounds = new Rect(size);
        if (Items is not { Count: > 0 } items || bounds.Width < 2 || bounds.Height < 2) return;

        foreach (var (rect, item) in Squarify(items, bounds))
        {
            _laid.Add(new Laid(rect, item, 0, BrushOf(item, 0), LabelFor(item, rect)));

            // The second level: a folder shows its content, one level deeper.
            if (item.Kind != ItemKind.Folder || rect.Width <= NestMinimum || rect.Height <= NestMinimum) continue;

            var inner = rect.Deflate(new Thickness(3, 18, 3, 3));
            if (inner.Width <= 8 || inner.Height <= 8) continue;

            foreach (var (childRect, child) in Squarify(TreemapItem.Of(item.Folder), inner))
                _laid.Add(new Laid(childRect, child, 1, BrushOf(child, 1), null));
        }
    }

    private static IBrush BrushOf(TreemapItem item, int depth)
    {
        var key = (item.Category, depth);
        if (!BrushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(ColorOf(item, depth)).ToImmutable();
            BrushCache[key] = brush;
        }

        return brush;
    }

    private static bool Same(TreemapItem? a, TreemapItem? b) =>
        a is not null && b is not null && a.Kind == b.Kind
        && ReferenceEquals(a.Folder, b.Folder) && ReferenceEquals(a.File, b.File)
        && a.Name == b.Name;

    private static Color ColorOf(TreemapItem item, int depth)
    {
        var color = Colors.TryGetValue(item.Category, out var c) ? c : Colors["File"];
        if (depth == 0) return color;

        // The inner level slightly lighter so it stands out from its folder.
        return Color.FromRgb(
            (byte)Math.Min(255, color.R + 22),
            (byte)Math.Min(255, color.G + 22),
            (byte)Math.Min(255, color.B + 22));
    }

    private static FormattedText? LabelFor(TreemapItem item, Rect rect)
    {
        if (rect.Width < 54 || rect.Height < 18) return null;

        var text = $"{item.Name}  {MultiTool.Abstractions.Humanize.Bytes(item.Size)}";
        return new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11.5, Avalonia.Media.Brushes.White)
        {
            MaxTextWidth = Math.Max(1, rect.Width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
    }

    /// <summary>After a change to the tree (delete, rescan) affecting the same list.</summary>
    public void Invalidate()
    {
        _laidFor = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ Input

    private TreemapItem? HitTest(Point point)
    {
        for (var i = _laid.Count - 1; i >= 0; i--)
            if (_laid[i].Rect.Contains(point)) return _laid[i].Item;

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var hit = HitTest(e.GetPosition(this));
        if (Same(hit, _hovered) || (hit is null && _hovered is null)) return;

        _hovered = hit;
        Hovered?.Invoke(hit);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
        Hovered?.Invoke(null);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var point = e.GetCurrentPoint(this);
        var hit = HitTest(point.Position);
        if (hit is null) return;

        Focus();
        SelectedItem = hit;

        if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2 && hit.Kind == ItemKind.Folder)
            Opened?.Invoke(hit);
    }

    // ------------------------------------------------------------------ Layout

    /// <summary>
    /// The share of area for a size: <c>size^0.4</c>. Monotonic, so the order stays; 440 GB against 1.5 GB
    /// becomes about 10 : 1 instead of 290 : 1.
    /// </summary>
    internal static double Weight(long size) => Math.Pow(Math.Max(0, size), 0.4);

    /// <summary>Splits <paramref name="bounds"/> among the entries by <see cref="Weight"/>.</summary>
    /// <remarks>
    /// A row grows as long as its worst aspect ratio improves, then it is laid along the shorter side
    /// of the remaining rectangle. Expects entries sorted largest first.
    /// </remarks>
    internal static List<(Rect Rect, TreemapItem Item)> Squarify(IReadOnlyList<TreemapItem> items, Rect bounds)
    {
        var result = new List<(Rect, TreemapItem)>(items.Count);

        var weights = new double[items.Count];
        var total = 0.0;
        for (var i = 0; i < items.Count; i++) total += weights[i] = Weight(items[i].Size);
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return result;

        var scale = bounds.Width * bounds.Height / total;
        var rest = bounds;
        var start = 0;

        while (start < items.Count && rest.Width > 0.5 && rest.Height > 0.5)
        {
            var side = Math.Min(rest.Width, rest.Height);
            var end = start + 1;
            var rowArea = weights[start] * scale;
            var worst = Worst(rowArea, weights[start] * scale, weights[start] * scale, side);

            while (end < items.Count)
            {
                var area = weights[end] * scale;
                var candidate = Worst(rowArea + area, weights[start] * scale, area, side);
                if (candidate > worst) break;

                rowArea += area;
                worst = candidate;
                end++;
            }

            if (rest.Width >= rest.Height)
            {
                // A column at the left edge.
                var width = Math.Min(rest.Width, rowArea / rest.Height);
                var y = rest.Y;

                for (var i = start; i < end; i++)
                {
                    var height = weights[i] * scale / width;
                    result.Add((new Rect(rest.X, y, width, height), items[i]));
                    y += height;
                }

                rest = new Rect(rest.X + width, rest.Y, Math.Max(0, rest.Width - width), rest.Height);
            }
            else
            {
                // A row at the top edge.
                var height = Math.Min(rest.Height, rowArea / rest.Width);
                var x = rest.X;

                for (var i = start; i < end; i++)
                {
                    var width = weights[i] * scale / height;
                    result.Add((new Rect(x, rest.Y, width, height), items[i]));
                    x += width;
                }

                rest = new Rect(rest.X, rest.Y + height, rest.Width, Math.Max(0, rest.Height - height));
            }

            start = end;
        }

        return result;
    }

    /// <summary>The worst aspect ratio of a row; 1 would be a square.</summary>
    /// <param name="sum">Area of the whole row.</param>
    /// <param name="largest">Largest single area (the first, sorted largest first).</param>
    /// <param name="smallest">Smallest single area (the last added).</param>
    private static double Worst(double sum, double largest, double smallest, double side)
    {
        var s2 = sum * sum;
        var w2 = side * side;
        return Math.Max(w2 * largest / s2, s2 / (w2 * Math.Max(smallest, 1e-9)));
    }
}
