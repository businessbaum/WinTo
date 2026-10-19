using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>
/// One internal format for the whole graph: 48 kHz, stereo, 32-bit float. Everything is converted on read so
/// the mixer never has to.
/// </summary>
public static class AudioFormat
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    public static readonly WaveFormat Engine = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static readonly string[] SupportedExtensions =
        [".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a file as a sample source. The caller must dispose <paramref name="owner"/>.</summary>
    public static ISampleProvider Open(string path, out IDisposable owner)
    {
        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            var vorbis = new NAudio.Vorbis.VorbisWaveReader(path);
            owner = vorbis;
            return vorbis;
        }

        // Covers wav/mp3/aiff natively and falls back to Media Foundation for everything else.
        var reader = new AudioFileReader(path);
        owner = reader;
        return reader;
    }

    /// <summary>Brings any source to 48 kHz stereo.</summary>
    public static ISampleProvider Normalize(ISampleProvider source)
    {
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        else if (source.WaveFormat.Channels > 2)
            source = new DownmixToStereoSampleProvider(source);

        if (source.WaveFormat.SampleRate != SampleRate)
            source = new WdlResamplingSampleProvider(source, SampleRate);

        return source;
    }

    public static ISampleProvider OpenNormalized(string path, out IDisposable owner) =>
        Normalize(Open(path, out owner));

    /// <summary>Reads only the length from the header, without decoding the file.</summary>
    public static TimeSpan? TryGetDuration(string path)
    {
        IDisposable? owner = null;
        try
        {
            AudioFormat.Open(path, out owner);
            return owner is WaveStream stream ? stream.TotalTime : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            owner?.Dispose();
        }
    }
}

/// <summary>
/// Downmixes any number of channels to stereo. NAudio only has StereoToMono; 5.1 files would otherwise fail
/// at the mixer.
/// </summary>
public sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _buffer = [];

    public DownmixToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / 2;
        var needed = frames * _sourceChannels;
        if (_buffer.Length < needed) _buffer = new float[needed];

        var read = _source.Read(_buffer, 0, needed);
        var readFrames = read / _sourceChannels;

        for (var f = 0; f < readFrames; f++)
        {
            var baseIndex = f * _sourceChannels;
            // Channels 0/1 carry L/R in multichannel layouts; the rest is mixed in evenly.
            float left = _buffer[baseIndex];
            float right = _buffer[baseIndex + 1];
            for (var c = 2; c < _sourceChannels; c++)
            {
                var extra = _buffer[baseIndex + c] * 0.7071f;
                left += extra;
                right += extra;
            }

            var scale = 1f / (1f + (_sourceChannels - 2) * 0.7071f);
            buffer[offset + f * 2] = left * scale;
            buffer[offset + f * 2 + 1] = right * scale;
        }

        return readFrames * 2;
    }
}
