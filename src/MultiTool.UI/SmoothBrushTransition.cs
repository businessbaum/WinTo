using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MultiTool.UI;

/// <summary>A brush transition that fades opacity instead of passing through white.</summary>
/// <remarks>
/// Avalonia's <see cref="BrushTransition"/> interpolates color channels and alpha separately.
/// <c>Transparent</c> is transparent <i>white</i>, so halfway to a dark hover color the brush is a
/// half-opaque light gray: a bright flash at the start of every hover on a ghost button.
/// Interpolating premultiplied colors keeps the hue and only fades the opacity.
/// </remarks>
public sealed class SmoothBrushTransition : InterpolatingTransitionBase<IBrush?>
{
    protected override IBrush? Interpolate(double progress, IBrush? from, IBrush? to)
    {
        if (ColorOf(from, to) is not { } a || ColorOf(to, from) is not { } b)
            return progress < 0.5 ? from : to;

        var p = Math.Clamp(progress, 0, 1);
        double fa = a.A / 255.0, ta = b.A / 255.0;
        var alpha = fa + (ta - fa) * p;
        if (alpha <= 0.0001) return new ImmutableSolidColorBrush(Color.FromArgb(0, b.R, b.G, b.B));

        byte Channel(byte from, byte to) =>
            (byte)Math.Clamp(Math.Round((from * fa + (to * ta - from * fa) * p) / alpha), 0, 255);

        return new ImmutableSolidColorBrush(Color.FromArgb(
            (byte)Math.Round(alpha * 255),
            Channel(a.R, b.R),
            Channel(a.G, b.G),
            Channel(a.B, b.B)));
    }

    /// <summary>The brush as one color with its opacity folded in. <c>null</c> is transparent.</summary>
    private static Color? ColorOf(IBrush? brush, IBrush? other) => brush switch
    {
        ISolidColorBrush solid => Color.FromArgb(
            (byte)Math.Round(solid.Color.A * Math.Clamp(solid.Opacity, 0, 1)),
            solid.Color.R, solid.Color.G, solid.Color.B),
        null when other is ISolidColorBrush => Colors.Transparent,
        _ => null,
    };
}
