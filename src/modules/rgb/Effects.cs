namespace MultiTool.Modules.Rgb;

/// <summary>Computes what each LED shows for a lighting at a moment in time.</summary>
/// <remarks>
/// <para>
/// <b>In software, on every source alike</b>: hardware effects differ per vendor and per device (one keyboard's
/// "wave" is another's "rainbow"), and Dynamic Lighting has none. The same frame goes everywhere, so devices from
/// different makers breathe and cycle in step.
/// </para>
/// <para>
/// Brightness is perceptual (gamma 2.2): LEDs are linear, eyes aren't, and a linear 50 % looks almost full.
/// </para>
/// </remarks>
public static class Effects
{
    public static void Render(Lighting lighting, float[] positions, double seconds, RgbColor[] output)
    {
        var color = RgbColor.Parse(lighting.Color, new RgbColor(124, 92, 255));
        var brightness = Perceptual(lighting.Brightness / 100d);
        var speed = Math.Clamp(lighting.Speed, 1, 100) / 100d;

        switch (lighting.Effect)
        {
            case RgbEffect.Off:
                Array.Fill(output, RgbColor.Black);
                break;

            case RgbEffect.Breathing:
            {
                // From 7 s per breath at the slowest to 1.2 s at the fastest; never fully dark.
                var period = Lerp(7, 1.2, speed);
                var phase = (1 - Math.Cos(2 * Math.PI * seconds / period)) / 2;
                Array.Fill(output, color.Scale(brightness * Perceptual(0.06 + 0.94 * phase)));
                break;
            }

            case RgbEffect.Spectrum:
            {
                var (_, saturation, _) = color.ToHsv();
                var hue = seconds * Lerp(12, 240, speed);
                Array.Fill(output, RgbColor.FromHsv(hue, Math.Max(saturation, 0.35), brightness));
                break;
            }

            case RgbEffect.Wave:
            {
                // One full rainbow across a device, moving left to right.
                var (_, saturation, _) = color.ToHsv();
                var shift = seconds * Lerp(40, 420, speed);
                for (var i = 0; i < output.Length; i++)
                {
                    var position = i < positions.Length ? positions[i] : 0.5f;
                    output[i] = RgbColor.FromHsv(position * 360 - shift, Math.Max(saturation, 0.35), brightness);
                }
                break;
            }

            default:
                Array.Fill(output, color.Scale(brightness));
                break;
        }
    }

    public static double Perceptual(double level) => Math.Pow(Math.Clamp(level, 0, 1), 2.2);

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;
}
