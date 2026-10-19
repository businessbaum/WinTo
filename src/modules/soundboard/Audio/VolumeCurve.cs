namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>
/// Translates slider positions into actual gain factors.
///
/// <para>
/// A slider setting amplitude directly feels wrong: loudness is perceived logarithmically. Half amplitude is
/// only −6 dB and barely quieter, while the whole audible range is squeezed into the bottom percent.
/// </para>
///
/// <para>
/// So the slider position is linear in <b>decibels</b>: 100 % is 0 dB (unchanged), 0 % is silence, and above
/// 100 % boosts. Equal slider steps mean equal perceived changes.
/// </para>
/// </summary>
public static class VolumeCurve
{
    /// <summary>Level at slider position 0. Below this it's fully muted.</summary>
    public const double MinDecibels = -48.0;

    /// <summary>Boost at double slider position (200 %).</summary>
    public const double MaxBoostDecibels = 12.0;

    private const double SilenceThreshold = 0.0005;

    /// <summary>Slider position (0 = mute, 1 = unchanged, &gt;1 = boosted) → gain factor.</summary>
    public static float ToAmplitude(double position)
    {
        if (position <= SilenceThreshold) return 0f;

        var decibels = position <= 1.0
            ? MinDecibels * (1.0 - position)
            : (position - 1.0) * MaxBoostDecibels;

        return (float)Math.Pow(10.0, decibels / 20.0);
    }

    /// <summary>Inverse, e.g. to adopt an old linearly stored value.</summary>
    public static double ToPosition(double amplitude)
    {
        if (amplitude <= SilenceThreshold) return 0.0;

        var decibels = 20.0 * Math.Log10(amplitude);

        return decibels <= 0.0
            ? Math.Clamp(1.0 - decibels / MinDecibels, 0.0, 1.0)
            : 1.0 + decibels / MaxBoostDecibels;
    }

    /// <summary>For display: the slider position in decibels.</summary>
    public static string Describe(double position)
    {
        if (position <= SilenceThreshold) return "muted";

        var decibels = position <= 1.0
            ? MinDecibels * (1.0 - position)
            : (position - 1.0) * MaxBoostDecibels;

        return decibels >= 0 ? $"+{decibels:0.0} dB" : $"{decibels:0.0} dB";
    }
}
