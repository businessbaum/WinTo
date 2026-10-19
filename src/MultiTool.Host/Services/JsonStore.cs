using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiTool.Host.Services;

/// <summary>Reading and writing JSON files.</summary>
/// <remarks>
/// Always written via a temp file and rename, so a crash mid-write can't leave half a file; the
/// last good version stays as <c>.bak</c>. Since 0.9.0 the backup is also read: before, a broken
/// file fell back to defaults and the next save turned the broken file into the backup.
/// </remarks>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Otherwise non-ASCII text is escaped; the files are meant to be readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // Enums as text ("elevation": "onDemand", not 1): manifests are written by hand, and a number
        // would silently change meaning when the enum is reordered.
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static T Load<T>(string path, Func<T> fallback) where T : class
    {
        // Whatever waits for the deferred write is newer than the file on disk.
        if (DeferredJson.TryGetPending(path, out var pending) && TryParse<T>(pending) is { } fresh)
            return fresh;

        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                if (TryParse<T>(File.ReadAllText(candidate)) is { } value) return value;
            }
            catch (Exception)
            {
                // A broken or locked file must not prevent startup: try the backup, then the default.
            }
        }

        return fallback();
    }

    private static T? TryParse<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Writes immediately. Throws if it still fails after several attempts.</summary>
    public static void Save<T>(string path, T value) where T : class => WriteText(path, Serialize(value));

    /// <summary>Writes text via a temp file and rename.</summary>
    /// <remarks>
    /// <para>
    /// With retries: an antivirus or the search indexer opens a fresh file for milliseconds, and a
    /// <c>File.Replace</c> at that moment fails with a sharing violation.
    /// </para>
    /// <para>
    /// The temp file carries the process id, so two instances (installed and dev build) never
    /// share one <c>.tmp</c>.
    /// </para>
    /// </remarks>
    public static void WriteText(string path, string json)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = $"{path}.{Environment.ProcessId}.tmp";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, json);

                if (File.Exists(path))
                {
                    // Replace is atomic and creates the backup in one step.
                    File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tmp, path);
                }

                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(30 * attempt);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                // Replace sometimes reports the same sharing violation this way.
                Thread.Sleep(30 * attempt);
            }
        }
    }
}

/// <summary>Writes JSON deferred, on a background thread.</summary>
/// <remarks>
/// <para>
/// Sliders used to save on every tick, synchronously on the UI thread. An antivirus scanning the
/// temp file turned that into an <see cref="IOException"/> from a property callback, which ended
/// the app including a running recording.
/// </para>
/// <para>
/// The text is produced right away at the caller (consistent state, microseconds); only the write
/// waits until nothing new arrived for <see cref="Quiet"/>.
/// </para>
/// <para>
/// One entry per file across all instances: a module released and reopened right away reads the
/// pending state via <see cref="TryGetPending"/>.
/// </para>
/// </remarks>
public static class DeferredJson
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Queues the text for writing.</summary>
    /// <param name="onError">Receives the reason if writing finally fails.</param>
    public static void Write(string path, string json, Action<Exception>? onError = null)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out var entry))
            {
                entry = new Entry(path);
                Entries[path] = entry;
            }

            entry.Json = json;
            entry.Version++;
            entry.OnError = onError;
            entry.Timer.Change(Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    public static bool TryGetPending(string path, out string json)
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(path, out var entry) && entry.Json is { } pending)
            {
                json = pending;
                return true;
            }
        }

        json = "";
        return false;
    }

    /// <summary>Writes what is pending for this file, now, on this thread.</summary>
    public static void Flush(string path)
    {
        Entry? entry;
        lock (Gate) Entries.TryGetValue(path, out entry);

        entry?.WriteNow();
    }

    /// <summary>Writes everything pending. On exit.</summary>
    public static void FlushAll()
    {
        Entry[] all;
        lock (Gate) all = [.. Entries.Values];

        foreach (var entry in all) entry.WriteNow();
    }

    private sealed class Entry
    {
        private readonly Lock _writeGate = new();
        private readonly string _path;
        private long _written;

        public Entry(string path)
        {
            _path = path;
            Timer = new Timer(_ => WriteNow(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public Timer Timer { get; }

        /// <summary>Read and written under <see cref="Gate"/>.</summary>
        public string? Json { get; set; }

        public long Version { get; set; }

        public Action<Exception>? OnError { get; set; }

        /// <summary>
        /// Writes the newest state. Timer and <see cref="Flush"/> may meet; the write lock keeps them apart
        /// and the version number keeps an older state from overwriting a newer one.
        /// </summary>
        public void WriteNow()
        {
            lock (_writeGate)
            {
                string? json;
                long version;
                Action<Exception>? onError;

                lock (Gate)
                {
                    json = Json;
                    version = Version;
                    onError = OnError;
                }

                if (json is null || version <= _written) return;

                try
                {
                    JsonStore.WriteText(_path, json);
                }
                catch (Exception ex)
                {
                    // The state stays queued; the next save or exit tries again.
                    try { onError?.Invoke(ex); } catch (Exception) { }
                    return;
                }

                _written = version;

                lock (Gate)
                {
                    // Only clear if nothing new arrived meanwhile.
                    if (Version == version) Json = null;
                }
            }
        }
    }
}
