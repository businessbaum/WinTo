using NAudio.Wave;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>A fully decoded sound in engine format (48 kHz stereo float).</summary>
public sealed class CachedSound
{
    public CachedSound(float[] data)
    {
        Data = data;
        Duration = TimeSpan.FromSeconds((double)data.Length / AudioFormat.Channels / AudioFormat.SampleRate);
    }

    public float[] Data { get; }

    public TimeSpan Duration { get; }

    public long Bytes => (long)Data.Length * sizeof(float);
}

/// <summary>
/// LRU cache for decoded PCM. Short sounds go to RAM (trigger latency ~0), long ones are streamed from disk
/// instead; a single 5-minute file would otherwise take over 100 MB.
/// </summary>
public sealed class SampleCache
{
    private readonly Dictionary<string, CachedSound> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly Dictionary<string, LinkedListNode<string>> _lruNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private long _bytes;

    public long BudgetBytes { get; set; } = 192L * 1024 * 1024;

    public double MaxCacheSeconds { get; set; } = 90;

    public long UsedBytes { get { lock (_gate) return _bytes; } }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>Returns the cached sound, or null if it isn't in RAM (yet).</summary>
    public CachedSound? Peek(string path)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var sound)) return null;
            Touch(path);
            return sound;
        }
    }

    /// <summary>Loads and caches the file. Returns null if it's too long to cache; then the caller must stream.</summary>
    public CachedSound? GetOrLoad(string path)
    {
        var cached = Peek(path);
        if (cached is not null) return cached;

        var sound = Decode(path);
        if (sound is null) return null;

        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var raced))
            {
                Touch(path);
                return raced;
            }

            _entries[path] = sound;
            _bytes += sound.Bytes;
            _lruNodes[path] = _lru.AddFirst(path);
            Evict();
        }

        return sound;
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            if (!_entries.Remove(path, out var sound)) return;
            _bytes -= sound.Bytes;
            if (_lruNodes.Remove(path, out var node)) _lru.Remove(node);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _lruNodes.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Decodes to 48 kHz stereo float. Null means too long, should be streamed.</summary>
    private CachedSound? Decode(string path)
    {
        IDisposable? owner = null;
        try
        {
            var source = AudioFormat.Open(path, out owner);

            // Length check up front so a 40 MB file isn't read at all.
            if (owner is WaveStream stream && stream.TotalTime > TimeSpan.FromSeconds(MaxCacheSeconds))
                return null;

            source = AudioFormat.Normalize(source);

            var maxSamples = (long)(MaxCacheSeconds * AudioFormat.SampleRate * AudioFormat.Channels);
            var chunks = new List<float[]>();
            var total = 0L;
            var chunk = new float[AudioFormat.SampleRate * AudioFormat.Channels]; // ~1 s
            int read;

            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                var slice = new float[read];
                Array.Copy(chunk, slice, read);
                chunks.Add(slice);
                total += read;
                if (total > maxSamples) return null; // length not readable from the header
            }

            var data = new float[total];
            var pos = 0;
            foreach (var c in chunks)
            {
                Array.Copy(c, 0, data, pos, c.Length);
                pos += c.Length;
            }

            return new CachedSound(data);
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

    private void Touch(string path)
    {
        if (!_lruNodes.TryGetValue(path, out var node)) return;
        _lru.Remove(node);
        _lru.AddFirst(node);
    }

    private void Evict()
    {
        while (_bytes > BudgetBytes && _lru.Last is { } oldest)
        {
            var path = oldest.Value;
            _lru.RemoveLast();
            _lruNodes.Remove(path);
            if (_entries.Remove(path, out var sound)) _bytes -= sound.Bytes;
            // Running voices still hold a reference to the float[], so removal doesn't interrupt playback.
        }
    }
}
