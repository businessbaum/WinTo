using Avalonia;
using Avalonia.Controls;

namespace MultiTool.Host.Views;

/// <summary>Lays out the open tools like browser tabs: all the same width, narrower as more open.</summary>
/// <remarks>
/// Every tab gets <see cref="MaxTabWidth"/> while there is room; beyond that the space is shared equally,
/// down to <see cref="MinTabWidth"/>, where the title is gone and the icon remains. The width is decided
/// in <c>MeasureOverride</c>, so titles are trimmed by their own layout and nothing is measured twice.
/// </remarks>
public sealed class TabStripPanel : Panel
{
    public static readonly StyledProperty<double> MaxTabWidthProperty =
        AvaloniaProperty.Register<TabStripPanel, double>(nameof(MaxTabWidth), 210);

    public static readonly StyledProperty<double> MinTabWidthProperty =
        AvaloniaProperty.Register<TabStripPanel, double>(nameof(MinTabWidth), 64);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<TabStripPanel, double>(nameof(Spacing), 2);

    static TabStripPanel()
    {
        AffectsMeasure<TabStripPanel>(MaxTabWidthProperty, MinTabWidthProperty, SpacingProperty);
    }

    public double MaxTabWidth
    {
        get => GetValue(MaxTabWidthProperty);
        set => SetValue(MaxTabWidthProperty, value);
    }

    public double MinTabWidth
    {
        get => GetValue(MinTabWidthProperty);
        set => SetValue(MinTabWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private double _tabWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var count = Children.Count(c => c.IsVisible);
        if (count == 0) return default;

        var gaps = Spacing * (count - 1);
        var share = double.IsInfinity(availableSize.Width) ? MaxTabWidth : (availableSize.Width - gaps) / count;
        _tabWidth = Math.Clamp(share, MinTabWidth, MaxTabWidth);

        var height = 0.0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;

            child.Measure(new Size(_tabWidth, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(_tabWidth * count + gaps, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;

            child.Arrange(new Rect(x, 0, _tabWidth, finalSize.Height));
            x += _tabWidth + Spacing;
        }

        return finalSize;
    }
}
