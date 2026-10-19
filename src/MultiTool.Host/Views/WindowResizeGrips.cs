using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace MultiTool.Host.Views;

/// <summary>Invisible resize zones at the window edges that compute the size themselves.</summary>
/// <remarks>
/// <para>
/// Needed since the windows use <c>WindowDecorations="None"</c>. Three dead ends first:
/// </para>
/// <list type="number">
///   <item><c>ExtendClientAreaToDecorationsHint</c> made the top pixels non-client area; the
///   header stopped receiving clicks.</item>
///   <item><c>BorderOnly</c> drew a visible frame over the header that resized instead of
///   dragging.</item>
///   <item>Restoring <c>WS_THICKFRAME</c> by hand made <c>BeginResizeDrag</c> work but brought the
///   non-client area back; the window could then neither be dragged nor resized.</item>
/// </list>
/// <para>
/// So the pointer is captured, the movement measured in screen pixels, and position and size
/// set directly. No window styles, no assumptions about Windows.
/// </para>
/// <para><b>No zone at the top</b>: the top edge belongs to dragging.</para>
/// </remarks>
internal static class WindowResizeGrips
{
    /// <summary>Width of an edge zone.</summary>
    private const double EdgeThickness = 5;

    /// <summary>Size of a corner zone; larger because a 5×5 corner is hard to hit.</summary>
    private const double CornerSize = 16;

    /// <summary>Lays the zones over a window's content.</summary>
    /// <param name="root">
    /// The window's root grid. Zones are added last, spanning all rows and columns, so they win
    /// hit testing.
    /// </param>
    public static void Attach(Window window, Grid root)
    {
        // Two layers: a 16 px corner in the same grid would make the edge row 16 px tall across
        // the full width.
        var edges = new Grid
        {
            RowDefinitions = new RowDefinitions($"*,{EdgeThickness}"),
            ColumnDefinitions = new ColumnDefinitions($"{EdgeThickness},*,{EdgeThickness}"),
        };

        AddEdge(edges, window, 1, 1, WindowEdge.South, StandardCursorType.SizeNorthSouth);
        AddEdge(edges, window, 0, 0, WindowEdge.West, StandardCursorType.SizeWestEast);
        AddEdge(edges, window, 0, 2, WindowEdge.East, StandardCursorType.SizeWestEast);

        var corners = new Grid();

        AddCorner(corners, window, HorizontalAlignment.Left, VerticalAlignment.Bottom,
            WindowEdge.SouthWest, StandardCursorType.BottomLeftCorner);
        AddCorner(corners, window, HorizontalAlignment.Right, VerticalAlignment.Bottom,
            WindowEdge.SouthEast, StandardCursorType.BottomRightCorner);

        foreach (var layer in new[] { edges, corners })
        {
            Grid.SetRow(layer, 0);
            Grid.SetColumn(layer, 0);
            Grid.SetRowSpan(layer, Math.Max(1, root.RowDefinitions.Count));
            Grid.SetColumnSpan(layer, Math.Max(1, root.ColumnDefinitions.Count));
            root.Children.Add(layer);
        }

        // A maximized window doesn't resize at the edge; the zone would only swallow clicks.
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty) return;

            var normal = window.WindowState == WindowState.Normal;
            edges.IsVisible = normal;
            corners.IsVisible = normal;
        };
    }

    private static void AddEdge(
        Grid layer, Window window, int row, int column,
        WindowEdge edge, StandardCursorType cursor)
    {
        var grip = NewGrip(window, edge, cursor);
        Grid.SetRow(grip, row);
        Grid.SetColumn(grip, column);
        layer.Children.Add(grip);
    }

    private static void AddCorner(
        Grid layer, Window window, HorizontalAlignment horizontal, VerticalAlignment vertical,
        WindowEdge edge, StandardCursorType cursor)
    {
        var grip = NewGrip(window, edge, cursor);
        grip.Width = CornerSize;
        grip.Height = CornerSize;
        grip.HorizontalAlignment = horizontal;
        grip.VerticalAlignment = vertical;
        layer.Children.Add(grip);
    }

    private static Border NewGrip(Window window, WindowEdge edge, StandardCursorType cursor)
    {
        var grip = new Border
        {
            // Transparent, not null: a null background is invisible to hit testing.
            Background = Brushes.Transparent,
            Cursor = new Cursor(cursor),
        };

        // Start in screen pixels: dragging the left edge moves the window origin, so a
        // window-relative measurement would lag behind.
        PixelPoint start = default;
        PixelRect frame = default;
        var dragging = false;

        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;

            start = window.PointToScreen(e.GetPosition(window));
            frame = new PixelRect(
                window.Position,
                PixelSize.FromSize(window.ClientSize, window.RenderScaling));

            dragging = true;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };

        grip.PointerMoved += (_, e) =>
        {
            if (!dragging) return;

            var now = window.PointToScreen(e.GetPosition(window));
            Apply(window, edge, frame, now.X - start.X, now.Y - start.Y);
            e.Handled = true;
        };

        grip.PointerReleased += (_, e) =>
        {
            if (!dragging) return;

            dragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        };

        grip.PointerCaptureLost += (_, _) => dragging = false;

        return grip;
    }

    /// <summary>Sets position and size from the movement since grabbing.</summary>
    /// <remarks>
    /// Computed from the <b>opposite, fixed</b> edge, so the window stays put when the minimum width
    /// kicks in instead of creeping to the right.
    /// </remarks>
    private static void Apply(Window window, WindowEdge edge, PixelRect frame, int dx, int dy)
    {
        var scaling = window.RenderScaling;
        var minWidth = (int)Math.Round(Math.Max(1, window.MinWidth) * scaling);
        var minHeight = (int)Math.Round(Math.Max(1, window.MinHeight) * scaling);

        var left = frame.X;
        var width = frame.Width;
        var height = frame.Height;
        var right = frame.X + frame.Width;

        if (edge is WindowEdge.West or WindowEdge.SouthWest)
        {
            left = Math.Min(frame.X + dx, right - minWidth);
            width = right - left;
        }
        else if (edge is WindowEdge.East or WindowEdge.SouthEast)
        {
            width = Math.Max(minWidth, frame.Width + dx);
        }

        if (edge is WindowEdge.South or WindowEdge.SouthWest or WindowEdge.SouthEast)
        {
            height = Math.Max(minHeight, frame.Height + dy);
        }

        if (left != frame.X) window.Position = new PixelPoint(left, frame.Y);

        window.Width = width / scaling;
        window.Height = height / scaling;
    }
}
