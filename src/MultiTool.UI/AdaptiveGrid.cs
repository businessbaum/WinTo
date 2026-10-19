using Avalonia;
using Avalonia.Controls;

namespace MultiTool.UI;

/// <summary>Equal columns that fill the width, as many as fit at <see cref="MinItemWidth"/>.</summary>
/// <remarks>
/// A <c>WrapPanel</c> with fixed item widths leaves a ragged gap on the right, different for every
/// group. Here the items stretch, so groups of different sizes still line up in the same columns.
/// </remarks>
public sealed class AdaptiveGrid : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(MinItemWidth), 240);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(ColumnSpacing), 12);

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<AdaptiveGrid, double>(nameof(RowSpacing), 12);

    static AdaptiveGrid()
    {
        AffectsMeasure<AdaptiveGrid>(MinItemWidthProperty, ColumnSpacingProperty, RowSpacingProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private (int Columns, double ItemWidth) Layout(double width)
    {
        var columns = Math.Max(1, (int)((width + ColumnSpacing) / (MinItemWidth + ColumnSpacing)));
        return (columns, Math.Max(0, (width - (columns - 1) * ColumnSpacing) / columns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? MinItemWidth : availableSize.Width;
        var (columns, itemWidth) = Layout(width);

        double height = 0, rowHeight = 0;
        var index = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;

            child.Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            if (++index % columns == 0)
            {
                height += rowHeight + RowSpacing;
                rowHeight = 0;
            }
        }

        height += index % columns == 0 ? (index > 0 ? -RowSpacing : 0) : rowHeight;
        return new Size(width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, itemWidth) = Layout(finalSize.Width);
        var visible = Children.Where(c => c.IsVisible).ToList();

        double y = 0;
        for (var start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);

            for (var i = 0; i < row.Count; i++)
                row[i].Arrange(new Rect(i * (itemWidth + ColumnSpacing), y, itemWidth, rowHeight));

            y += rowHeight + RowSpacing;
        }

        return finalSize;
    }
}
