using NAudio.Wave;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>A running voice in the mixer: volume, short fades against clicks, and a stop that leaves the mixer cleanly.</summary>
/// <remarks>
/// <see cref="MixingSampleProvider"/> removes a source as soon as it delivers fewer samples than requested.
/// That's used for cleanup; no lock is needed on the audio thread.
/// </remarks>
internal sealed class Voice : ISampleProvider
{
    private const int FadeInFrames = 144;   // 3 ms
    private const int FadeOutFrames = 384;  // 8 ms
    private const int PauseFadeFrames = 240; // 5 ms, against clicks when pausing

    private readonly ISampleProvider _source;
    private readonly IDisposable? _owner;
    private readonly IPositionedSource? _positioned;

    private int _fadeInPos;
    private int _fadeOutPos = -1;
    private int _pauseFade;             // 0 = fully audible, PauseFadeFrames = silent
    private volatile bool _stopRequested;
    private volatile bool _paused;
    private int _finished;

    public Voice(ISampleProvider source, IDisposable? owner, float volume)
    {
        _source = source;
        _owner = owner;
        _positioned = source as IPositionedSource;
        Volume = volume;
    }

    public WaveFormat WaveFormat => AudioFormat.Engine;

    public float Volume { get; set; }

    public bool IsPaused => _paused;

    public TimeSpan Position => _positioned?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _positioned?.Duration ?? TimeSpan.Zero;

    /// <summary>Raised on the audio thread; receivers must marshal themselves.</summary>
    public event EventHandler? Finished;

    public void Stop() => _stopRequested = true;

    public void SetPaused(bool paused) => _paused = paused;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_finished == 1) return 0;

        if (_stopRequested && _fadeOutPos < 0) _fadeOutPos = 0;

        // Paused: deliver silence but return the full length so the mixer keeps the voice. The
        // source isn't read, so the position stays.
        if (_paused && _fadeOutPos < 0 && _pauseFade >= PauseFadeFrames)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            Finish();
            return 0;
        }

        var frames = read / AudioFormat.Channels;
        var volume = Volume;

        for (var f = 0; f < frames; f++)
        {
            var gain = volume;

            if (_fadeInPos < FadeInFrames)
            {
                gain *= (float)_fadeInPos / FadeInFrames;
                _fadeInPos++;
            }

            // Soft ramp into the pause and out again.
            if (_paused && _pauseFade < PauseFadeFrames) _pauseFade++;
            else if (!_paused && _pauseFade > 0) _pauseFade--;

            if (_pauseFade > 0) gain *= 1f - (float)_pauseFade / PauseFadeFrames;

            if (_fadeOutPos >= 0)
            {
                if (_fadeOutPos >= FadeOutFrames)
                {
                    // Faded out: discard the rest and return a partial block so the mixer removes us.
                    var produced = f * AudioFormat.Channels;
                    Finish();
                    return produced;
                }

                gain *= 1f - (float)_fadeOutPos / FadeOutFrames;
                _fadeOutPos++;
            }

            var i = offset + f * AudioFormat.Channels;
            buffer[i] *= gain;
            buffer[i + 1] *= gain;
        }

        if (read < count) Finish();
        return read;
    }

    /// <summary>Ends the voice immediately, without waiting for the audio thread.</summary>
    /// <remarks>
    /// For when the output branch disappears before it's read again: device change and engine Dispose.
    /// <see cref="Finish"/> otherwise only runs from <see cref="Read"/>; without this path a streamed sound would
    /// stay "active" forever with its file handle open.
    /// </remarks>
    public void FinishNow() => Finish();

    private void Finish()
    {
        // Interlocked instead of bool: FinishNow comes from the engine thread, Read from the audio
        // thread. Owner dispose and Finished must run exactly once.
        if (Interlocked.Exchange(ref _finished, 1) == 1) return;

        try { _owner?.Dispose(); }
        catch (Exception) { /* file handle already gone; irrelevant */ }

        Finished?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A source that can report its playback position.</summary>
internal interface IPositionedSource : ISampleProvider
{
    TimeSpan Position { get; }

    /// <summary><see cref="TimeSpan.Zero"/> if the length is unknown.</summary>
    TimeSpan Duration { get; }
}

/// <summary>Plays from the RAM cache. No I/O, no allocation on the audio thread.</summary>
internal sealed class CachedSoundSource : IPositionedSource
{
    private readonly CachedSound _sound;
    private readonly bool _loop;
    private int _position;

    public CachedSoundSource(CachedSound sound, bool loop)
    {
        _sound = sound;
        _loop = loop;
    }

    public WaveFormat WaveFormat => AudioFormat.Engine;

    public TimeSpan Position => TimeSpan.FromSeconds(
        (double)Volatile.Read(ref _position) / AudioFormat.Channels / AudioFormat.SampleRate);

    public TimeSpan Duration => _sound.Duration;

    public int Read(float[] buffer, int offset, int count)
    {
        var data = _sound.Data;
        if (data.Length == 0) return 0;

        var total = 0;
        while (total < count)
        {
            var available = data.Length - _position;
            if (available <= 0)
            {
                if (!_loop) break;
                _position = 0;
                continue;
            }

            var n = Math.Min(count - total, available);
            Array.Copy(data, _position, buffer, offset + total, n);
            _position += n;
            total += n;
        }

        return total;
    }
}

/// <summary>Streams long files from disk instead of loading them fully into RAM.</summary>
internal sealed class StreamingSource : IPositionedSource, IDisposable
{
    private readonly bool _loop;
    private readonly string _path;
    private IDisposable? _owner;
    private ISampleProvider? _chain;
    private bool _disposed;

    public StreamingSource(string path, bool loop)
    {
        _path = path;
        _loop = loop;
        _chain = AudioFormat.OpenNormalized(path, out var owner);
        _owner = owner;
    }

    public WaveFormat WaveFormat => AudioFormat.Engine;

    // Position and length come straight from the file reader. Resampling buffers make the
    // value run a few milliseconds ahead of the output; irrelevant for a progress display.
    public TimeSpan Position => SafeTime(s => s.CurrentTime);

    public TimeSpan Duration => SafeTime(s => s.TotalTime);

    private TimeSpan SafeTime(Func<WaveStream, TimeSpan> read)
    {
        try { return _owner is WaveStream stream ? read(stream) : TimeSpan.Zero; }
        catch (Exception) { return TimeSpan.Zero; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_disposed || _chain is null) return 0;

        var total = 0;
        while (total < count)
        {
            var read = _chain.Read(buffer, offset + total, count - total);
            if (read <= 0)
            {
                if (!_loop || !Restart()) break;
                continue;
            }

            total += read;
        }

        return total;
    }

    private bool Restart()
    {
        try
        {
            _owner?.Dispose();
            _chain = AudioFormat.OpenNormalized(_path, out var owner);
            _owner = owner;
            return true;
        }
        catch (Exception)
        {
            _chain = null;
            _owner = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _owner?.Dispose(); } catch (Exception) { /* irrelevant */ }
        _owner = null;
        _chain = null;
    }
}
