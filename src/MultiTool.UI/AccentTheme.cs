using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MultiTool.UI;

/// <summary>Recolors everything that follows the accent: buttons, switches, sliders, selection, the logo.</summary>
/// <remarks>
/// <para>
/// <b>Two kinds of resource, two ways.</b> Colors (<c>AccentColor</c>, Fluent's <c>SystemAccentColor*</c>)
/// are replaced in the application's top-level dictionary; everything using them through
/// <c>DynamicResource</c> follows. Brushes are changed <b>in place</b>: the palette builds them from
/// <c>StaticResource</c> colors, and controls holding the instance via <c>StaticResource</c> would never see a
/// replacement. A changed brush redraws whatever uses it.
/// </para>
/// <para>
/// <b>Derived, not picked.</b> Hover, pressed, soft background and the Fluent light and dark steps are mixed
/// from the one color, with the same ratios that produce the default violet's hand-picked values within a
/// few points. Text on the accent turns dark on a light accent.
/// </para>
/// </remarks>
public static class AccentTheme
{
    public static readonly Color Default = Color.Parse("#7C5CFF");

    /// <summary>The accent currently applied.</summary>
    public static Color Current { get; private set; } = Default;

    private static readonly Color White = Colors.White;
    private static readonly Color Black = Colors.Black;
    private static readonly Color DarkText = Color.Parse("#14161C");

    public static void Apply(Color accent)
    {
        if (Application.Current is not { } app) return;

        accent = Color.FromRgb(accent.R, accent.G, accent.B);
        Current = accent;

        var background = app.TryGetResource("BgBaseColor", null, out var bg) && bg is Color c ? c : Color.Parse("#101118");

        var hover = Mix(accent, White, 0.14);
        var pressed = Mix(accent, Black, 0.10);
        var soft = Mix(background, accent, 0.20);
        var onAccent = Luminance(accent) > 0.4 ? DarkText : White;

        SetColor(app, "AccentColor", accent);
        SetColor(app, "AccentHoverColor", hover);
        SetColor(app, "AccentPressedColor", pressed);
        SetColor(app, "AccentSoftColor", soft);
        SetColor(app, "SystemAccentColor", accent);
        SetColor(app, "SystemAccentColorLight1", Mix(accent, White, 0.14));
        SetColor(app, "SystemAccentColorLight2", Mix(accent, White, 0.28));
        SetColor(app, "SystemAccentColorLight3", Mix(accent, White, 0.42));
        SetColor(app, "SystemAccentColorDark1", Mix(accent, Black, 0.10));
        SetColor(app, "SystemAccentColorDark2", Mix(accent, Black, 0.20));
        SetColor(app, "SystemAccentColorDark3", Mix(accent, Black, 0.30));

        foreach (var key in (string[])["AccentBrush", "TextControlBorderBrushFocused", "TextControlSelectionHighlightColor",
                     "ComboBoxBorderBrushPressed", "SliderTrackValueFill", "SliderThumbBackground", "ToggleSwitchFillOn",
                     "ToggleSwitchStrokeOn"])
            SetBrush(app, key, accent);

        foreach (var key in (string[])["AccentHoverBrush", "SliderTrackValueFillPointerOver", "SliderThumbBackgroundPointerOver",
                     "ToggleSwitchFillOnPointerOver"])
            SetBrush(app, key, hover);

        foreach (var key in (string[])["AccentPressedBrush", "SliderTrackValueFillPressed", "SliderThumbBackgroundPressed",
                     "ToggleSwitchFillOnPressed"])
            SetBrush(app, key, pressed);

        foreach (var key in (string[])["AccentSoftBrush", "ComboBoxItemBackgroundSelected", "ComboBoxItemBackgroundSelectedPointerOver"])
            SetBrush(app, key, soft);

        SetBrush(app, "OnAccentBrush", onAccent);
        SetBrush(app, "OnAccentMutedBrush", Color.FromArgb(0xD8, onAccent.R, onAccent.G, onAccent.B));

        SetStops(app, "AccentGradientBrush", Mix(accent, White, 0.14), Mix(accent, Black, 0.20));

        // The logo keeps the app icon's colors (LogoGradientBrush): the header matches the taskbar.

        var clear = Color.FromArgb(0, accent.R, accent.G, accent.B);
        SetStops(app, "BusySweepBrush", clear, accent, Mix(accent, White, 0.45), clear, clear);
    }

    /// <summary>A color from a stored setting: <c>#RRGGBB</c>, or the default when empty or unreadable.</summary>
    public static Color Parse(string? value) =>
        value is { Length: > 0 } && Color.TryParse(value, out var color) ? color : Default;

    /// <summary><c>#RRGGBB</c>, as stored and shown.</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void SetColor(Application app, string key, Color color) => app.Resources[key] = color;

    private static void SetBrush(Application app, string key, Color color)
    {
        if (app.TryGetResource(key, null, out var value) && value is SolidColorBrush brush)
            brush.Color = color;
        else
            app.Resources[key] = new SolidColorBrush(color);
    }

    private static void SetStops(Application app, string key, params Color[] colors)
    {
        if (!app.TryGetResource(key, null, out var value) || value is not GradientBrush brush) return;

        for (var i = 0; i < brush.GradientStops.Count && i < colors.Length; i++)
            brush.GradientStops[i].Color = colors[i];
    }

    /// <summary>Linear mix in sRGB: <paramref name="amount"/> 0 is <paramref name="from"/>, 1 is <paramref name="to"/>.</summary>
    public static Color Mix(Color from, Color to, double amount) => Color.FromArgb(
        (byte)Math.Round(from.A + (to.A - from.A) * amount),
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>Relative luminance (WCAG), 0 black to 1 white.</summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }
}
