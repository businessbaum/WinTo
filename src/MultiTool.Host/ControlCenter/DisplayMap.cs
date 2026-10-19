using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MultiTool.Host.ControlCenter;

/// <summary>A thumbnail of the display arrangement with one display filled in: which slider is which screen.</summary>
public sealed class DisplayMap : Control
{
    public static readonly StyledProperty<IReadOnlyList<DisplaySpot>?> SpotsProperty =
        AvaloniaProperty.Register<DisplayMap, IReadOnlyList<DisplaySpot>?>(nameof(Spots));

    public static readonly StyledProperty<DisplaySpot?> HighlightProperty =
        AvaloniaProperty.Register<DisplayMap, DisplaySpot?>(nameof(Highlight));

    static DisplayMap()
    {
        AffectsRender<DisplayMap>(SpotsProperty, HighlightProperty);
    }

    public IReadOnlyList<DisplaySpot>? Spots
    {
        get => GetValue(SpotsProperty);
        set => SetValue(SpotsProperty, value);
    }

    public DisplaySpot? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Spots is not { Count: > 0 } spots || Bounds.Width < 4 || Bounds.Height < 4) return;

        var left = spots.Min(s => s.X);
        var top = spots.Min(s => s.Y);
        var width = spots.Max(s => s.X + s.Width) - left;
        var height = spots.Max(s => s.Y + s.Height) - top;
        if (width <= 0 || height <= 0) return;

        var scale = Math.Min(Bounds.Width / width, Bounds.Height / height);
        var offsetX = (Bounds.Width - width * scale) / 2;
        var offsetY = (Bounds.Height - height * scale) / 2;

        var accent = Brush("AccentBrush", Brushes.MediumPurple);
        var idle = Brush("BgActiveBrush", Brushes.DimGray);
        var edge = new Pen(Brush("BorderStrongBrush", Brushes.Gray), 1);

        foreach (var spot in spots)
        {
            var rect = new Rect(
                offsetX + (spot.X - left) * scale,
                offsetY + (spot.Y - top) * scale,
                spot.Width * scale,
                spot.Height * scale).Deflate(1);

            var highlighted = Highlight is { } h && h.Monitor == spot.Monitor;
            context.DrawRectangle(highlighted ? accent : idle, highlighted ? null : edge, rect, 2, 2);
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
