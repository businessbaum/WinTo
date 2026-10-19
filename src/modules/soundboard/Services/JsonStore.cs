using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;

namespace MultiTool.Modules.Soundboard.Services;

/// <summary>
/// Loads and saves the JSON files. Always writes via a temp file and renames, so a crash mid-save can't
/// corrupt the configuration. The last good version stays as .bak.
/// </summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static T Load<T>(string path, Func<T> fallback) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var json = File.ReadAllText(candidate);
                var value = JsonSerializer.Deserialize<T>(json, Options);
                if (value is not null) return value;
            }
            catch (Exception)
            {
                // Broken file: next candidate, finally the default.
            }
        }

        return fallback();
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static bool Save<T>(string path, T value)
    {
        try
        {
            return WriteText(path, Serialize(value));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Writes finished text. Retries on a brief antivirus lock.</summary>
    public static bool WriteText(string path, string json)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var temp = path + ".tmp";
                File.WriteAllText(temp, json);

                if (File.Exists(path))
                {
                    var backup = path + ".bak";
                    File.Copy(path, backup, overwrite: true);
                }

                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(30 * attempt);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }
}

/// <summary>Collects save requests and writes only after a short quiet period. A volume slider would otherwise trigger hundreds of writes.</summary>
/// <remarks>
/// <para>
/// <b>Serialized on the UI thread, written on a background thread.</b> Until 0.9.0 both ran in the timer on a
/// thread pool thread while the UI thread modified the same lists. An import or move at the wrong moment
/// gave "Collection was modified", saving failed silently, and the state wasn't on disk until the next change.
/// </para>
/// <para>
/// On the UI thread settings and library are consistent, since only it modifies them. Serializing costs
/// microseconds to a few milliseconds; the write, which can wait, stays in the background.
/// </para>
/// </remarks>
public sealed class DebouncedSaver : IDisposable
{
    private readonly Func<string> _serialize;
    private readonly Func<string, bool> _write;
    private readonly int _delayMs;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private bool _pending;
    private bool _disposed;

    public DebouncedSaver(Func<string> serialize, Func<string, bool> write, int delayMs = 700)
    {
        _serialize = serialize;
        _write = write;
        _delayMs = delayMs;
        _timer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Request()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = true;
            _timer.Change(_delayMs, Timeout.Infinite);
        }
    }

    /// <summary>Writes pending changes immediately. UI thread only.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_pending) return;
            _pending = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (Snapshot() is { } json) Write(json);
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed || !_pending) return;
            _pending = false;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Snapshot() is not { } json) return;
            _ = Task.Run(() => Write(json));
        });
    }

    private string? Snapshot()
    {
        try { return _serialize(); }
        catch (Exception) { return null; /* saving must never kill the app */ }
    }

    private void Write(string json)
    {
        // Timer and Flush can meet; both write the same .tmp.
        lock (_writeGate)
        {
            try { _write(json); }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Flush();
        _timer.Dispose();
    }
}
