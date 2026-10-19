using NAudio.Wave;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>Catches clipping before it reaches the output device.</summary>
/// <remarks>
/// <para>
/// The sum of several simultaneous sounds, raised levels and the microphone can leave the ±1.0 range. WASAPI
/// hard-clips everything above: audible crackle and distortion, exactly at the loudest moments.
/// </para>
/// <para>
/// Instead of hard clipping, everything above a threshold is compressed softly. Below it the signal stays
/// <b>completely untouched</b>; above it a tanh curve approaches 1.0 asymptotically. A pure curve without gain
/// control: no pumping and no lookahead delay.
/// </para>
/// </remarks>
public sealed class SoftLimiterSampleProvider : ISampleProvider
{
    /// <summary>Compression starts here. Nothing happens below.</summary>
    private const float Threshold = 0.80f;

    private const float Range = 1f - Threshold;

    private readonly ISampleProvider _source;
    private int _engaged;

    public SoftLimiterSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool Enabled { get; set; } = true;

    /// <summary>Has the protection engaged since the last query? Resets on read so the indicator doesn't stick.</summary>
    public bool ConsumeEngaged() => Interlocked.Exchange(ref _engaged, 0) != 0;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (!Enabled || read <= 0) return read;

        var engaged = false;

        for (var i = offset; i < offset + read; i++)
        {
            var sample = buffer[i];
            var magnitude = Math.Abs(sample);
            if (magnitude <= Threshold) continue;

            var excess = (magnitude - Threshold) / Range;
            buffer[i] = MathF.CopySign(Threshold + Range * MathF.Tanh(excess), sample);
            engaged = true;
        }

        if (engaged) Interlocked.Exchange(ref _engaged, 1);
        return read;
    }
}
