using System.Globalization;

namespace MultiTool.Modules.Rgb;

/// <summary>A color as LEDs take it: three bytes, no alpha.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static readonly RgbColor Black = new(0, 0, 0);

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Hue in degrees, saturation and value from 0 to 1.</summary>
    public static RgbColor FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = value - c;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };

        return new RgbColor(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    public (double Hue, double Saturation, double Value) ToHsv()
    {
        double r = R / 255d, g = G / 255d, b = B / 255d;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        var hue = delta == 0 ? 0
            : max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * ((b - r) / delta + 2)
            : 60 * ((r - g) / delta + 4);

        return ((hue + 360) % 360, max == 0 ? 0 : delta / max, max);
    }

    /// <summary>Darker by <paramref name="factor"/> (0 to 1).</summary>
    public RgbColor Scale(double factor) =>
        new(Byte(R / 255d * factor), Byte(G / 255d * factor), Byte(B / 255d * factor));

    /// <summary><c>#RRGGBB</c> or <c>RRGGBB</c>.</summary>
    public static bool TryParse(string? text, out RgbColor color)
    {
        color = Black;
        var hex = text?.Trim().TrimStart('#') ?? "";
        if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;

        color = new RgbColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public static RgbColor Parse(string? text, RgbColor fallback) => TryParse(text, out var color) ? color : fallback;

    private static byte Byte(double unit) => (byte)Math.Round(Math.Clamp(unit, 0, 1) * 255);
}

/// <summary>What a device is, for its icon and the order in the list.</summary>
public enum RgbKind
{
    Keyboard,
    Mouse,
    Headset,
    Mousepad,
    Controller,
    Motherboard,
    Memory,
    Graphics,
    Cooler,
    Strip,
    Case,
    Speaker,
    Microphone,
    Other,
}

public enum RgbEffect
{
    Static,
    Breathing,
    Spectrum,
    Wave,
    Off,
}

/// <summary>What one device, or all of them, show.</summary>
public sealed class Lighting
{
    public RgbEffect Effect { get; set; } = RgbEffect.Static;

    /// <summary><c>#RRGGBB</c>; Spectrum and Wave run through all hues and use only its saturation.</summary>
    public string Color { get; set; } = "#7C5CFF";

    /// <summary>1 to 100, for the moving effects.</summary>
    public int Speed { get; set; } = 40;

    /// <summary>0 to 100.</summary>
    public int Brightness { get; set; } = 100;

    public Lighting Clone() => new() { Effect = Effect, Color = Color, Speed = Speed, Brightness = Brightness };

    public bool Moves => Effect is RgbEffect.Breathing or RgbEffect.Spectrum or RgbEffect.Wave;
}

/// <summary>The module's <c>settings.json</c>.</summary>
public sealed class RgbSettings
{
    /// <summary>For every device that has no lighting of its own.</summary>
    public Lighting All { get; set; } = new();

    /// <summary>Devices set apart from <see cref="All"/>, by <see cref="RgbDevice.Key"/>.</summary>
    public Dictionary<string, Lighting> Devices { get; set; } = [];

    /// <summary>Devices WinTo leaves alone, by <see cref="RgbDevice.Key"/>.</summary>
    public List<string> Excluded { get; set; } = [];
}

/// <summary>One thing with lights, from one source.</summary>
/// <remarks>
/// <see cref="Write"/> runs on the engine's thread (an MTA thread, which Dynamic Lighting needs), never on the UI
/// thread; properties read by the view are set once at discovery or are cheap and thread-safe.
/// </remarks>
public abstract class RgbDevice
{
    protected RgbDevice(string source, string key, string name, RgbKind kind, int ledCount)
    {
        Source = source;
        Key = key;
        Name = name;
        Kind = kind;
        LedCount = Math.Max(1, ledCount);
        Positions = [.. Enumerable.Range(0, LedCount).Select(i => LedCount == 1 ? 0.5f : i / (float)(LedCount - 1))];
    }

    public string Source { get; }

    /// <summary>Stable across starts: the settings remember devices by it.</summary>
    public string Key { get; }

    public string Name { get; }

    public RgbKind Kind { get; }

    public int LedCount { get; }

    /// <summary>Each LED from 0 (left) to 1 (right), for the wave. Spread evenly where the device says nothing.</summary>
    public float[] Positions { get; protected init; }

    /// <summary>How often an effect may update it; local HTTP sources want fewer frames than USB.</summary>
    public virtual TimeSpan FrameInterval => TimeSpan.FromMilliseconds(33);

    /// <summary>Whether writes reach the device now; Dynamic Lighting only while WinTo is in front or allowed.</summary>
    public virtual bool Available => true;

    /// <summary>A short note under the name when something limits it.</summary>
    public virtual string? Note => null;

    /// <summary>On the engine thread; <paramref name="colors"/> has <see cref="LedCount"/> entries.</summary>
    public abstract void Write(RgbColor[] colors);
}

public enum SourceHealth
{
    /// <summary>Connected; the devices are listed.</summary>
    Ready,

    /// <summary>The software behind it isn't there or isn't running.</summary>
    Missing,

    /// <summary>It answered with an error.</summary>
    Problem,
}

public sealed record SourceResult(IReadOnlyList<RgbDevice> Devices, SourceHealth Health, string State);

/// <summary>One way to reach lights: Windows, OpenRGB, a vendor's software.</summary>
public interface IRgbSource : IDisposable
{
    string Name { get; }

    /// <summary>Finds what is there now. Called again on refresh; earlier devices are released first.</summary>
    Task<SourceResult> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>The device list changed on the other side (OpenRGB detected more, a connection dropped).</summary>
    event Action? Changed;
}
