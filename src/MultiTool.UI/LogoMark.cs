using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace MultiTool.UI;

/// <summary>The app logo at any size: a white switch on a near-black plate, like the app icon.</summary>
/// <remarks>
/// Shape and plate come from <c>LogoShape</c> and <c>LogoGradientBrush</c>. A faint light rim keeps the dark
/// plate apart from the dark header behind it, as the icon's rim does on a dark taskbar.
/// </remarks>
public sealed class LogoMark : Border
{
    private static readonly IBrush Rim = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)).ToImmutable();

    private readonly Viewbox _box;

    public LogoMark()
    {
        Width = Height = 24;
        BorderBrush = Rim;
        BorderThickness = new Thickness(1);
        this.Bind(BackgroundProperty, this.GetResourceObservable("LogoGradientBrush"));

        var mark = new Path { Fill = Brushes.White };
        mark.Bind(Path.DataProperty, this.GetResourceObservable("LogoShape"));

        _box = new Viewbox { Child = new Canvas { Width = 24, Height = 24, Children = { mark } } };
        Child = _box;
    }

    // Same proportions as the .ico: a 22.5 % corner, the mark on the inner 72 % (76 % when small).
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var side = Math.Min(e.NewSize.Width, e.NewSize.Height);
        CornerRadius = new CornerRadius(side * 0.225);
        _box.Margin = new Thickness(side * (side <= 24 ? 0.10 : 0.14) - BorderThickness.Left);
    }
}
