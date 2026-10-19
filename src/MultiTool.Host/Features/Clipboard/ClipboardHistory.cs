using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MultiTool.Host.Services;

namespace MultiTool.Host.Features.Clipboard;

public enum ClipKind
{
    Text,
    Image,
    Files,
}

/// <summary>One entry as stored in <c>history.json</c>.</summary>
public sealed class ClipRecord
{
    public string Id { get; set; } = "";
    public ClipKind Kind { get; set; }
    public string? Text { get; set; }
    public List<string>? Files { get; set; }

    /// <summary>File name below <c>clipboard\images</c>.</summary>
    public string? Image { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>The last time it was copied (or pasted from the panel, which copies it again).</summary>
    public DateTime Copied { get; set; }

    /// <summary>The first time; entries from before 0.13.0 have none and use <see cref="Copied"/>.</summary>
    public DateTime? FirstCopied { get; set; }

    /// <summary>How often; 0 in entries from before 0.13.0, which counts as once.</summary>
    public int Copies { get; set; }

    public bool Pinned { get; set; }
    public string Hash { get; set; } = "";
    public string? Source { get; set; }

    /// <summary>Formatting is stored next to the text in <c>clipboard\rich</c> (HTML, RTF).</summary>
    public bool Rich { get; set; }

    public int CopyCount => Math.Max(1, Copies);

    public DateTime FirstCopy => FirstCopied ?? Copied;
}

internal sealed class ClipHistoryFile
{
    public List<ClipRecord> Entries { get; set; } = [];
}

/// <summary>The stored history: newest first, pinned entries kept, the rest limited.</summary>
/// <remarks>
/// <para>
/// <b>On disk, under <c>%APPDATA%\WinTo\clipboard</c></b>: <c>history.json</c> plus one PNG per image and the
/// formatting of a text in <c>rich\</c>. It survives a restart, which is what makes a history useful; the
/// Features page can clear it. Formatting stays out of the JSON: a copied web paragraph brings tens of
/// kilobytes of HTML, and the JSON is rewritten on every copy.
/// </para>
/// <para>
/// <b>Copying the same thing again moves it up</b> and counts it, instead of adding a second entry (same content
/// hash). Pasting from the panel puts the entry on the clipboard, which counts as copying it again; that copy
/// comes from WinTo itself and keeps the entry's source and formatting.
/// </para>
/// <para>
/// <b>What is not kept</b> (0.13.0, as in Maccy): copies while paused, from ignored programs, text matching an
/// ignored pattern, and kinds switched off. Copies marked private by password managers never reach this class.
/// </para>
/// <para>
/// <b>All work off the UI thread, one change after another.</b> Converting a DIB and writing a PNG takes
/// tens of milliseconds; changes are queued so two quick copies keep their order.
/// </para>
/// </remarks>
internal sealed class ClipboardHistory
{
    public const int MaxImages = 30;

    /// <summary>Copies owned by this process are pastes from the panel.</summary>
    private static readonly string OwnProcess = CurrentProcessName();

    private static string CurrentProcessName()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return process.ProcessName;
    }

    private readonly Lock _gate = new();
    private readonly string _file;
    private readonly Func<ClipboardSettings> _settings;
    private List<ClipRecord> _entries = [];
    private Task _queue = Task.CompletedTask;
    private bool _loaded;

    public ClipboardHistory(Func<ClipboardSettings> settings)
    {
        _settings = settings;
        Folder = Path.Combine(AppPaths.DataDirectory, "clipboard");
        ImageFolder = Path.Combine(Folder, "images");
        RichFolder = Path.Combine(Folder, "rich");
        _file = Path.Combine(Folder, "history.json");
    }

    public string Folder { get; }

    public string ImageFolder { get; }

    public string RichFolder { get; }

    /// <summary>The limit for unpinned entries, from the settings.</summary>
    public int MaxEntries => Math.Clamp(_settings().MaxEntries, 10, 5000);

    /// <summary>Something was added, removed or pinned. Raised on any thread.</summary>
    public event Action? Changed;

    public IReadOnlyList<ClipRecord> Entries
    {
        get
        {
            lock (_gate) return [.. _entries];
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public string? ImagePath(ClipRecord record) => record.Image is { } name ? Path.Combine(ImageFolder, name) : null;

    private string HtmlPath(string id) => Path.Combine(RichFolder, id + ".html");

    private string RtfPath(string id) => Path.Combine(RichFolder, id + ".rtf");

    /// <summary>The formatting of a text entry, read at paste time. Both <c>null</c> for plain text.</summary>
    public (string? Html, byte[]? Rtf) ReadRich(ClipRecord record)
    {
        if (!record.Rich) return (null, null);

        try
        {
            var html = File.Exists(HtmlPath(record.Id)) ? File.ReadAllText(HtmlPath(record.Id), Encoding.UTF8) : null;
            var rtf = File.Exists(RtfPath(record.Id)) ? File.ReadAllBytes(RtfPath(record.Id)) : null;
            return (html, rtf);
        }
        catch (Exception)
        {
            // Without its formatting the text still pastes.
            return (null, null);
        }
    }

    /// <summary>Reads the file once, in the queue, so additions that arrive meanwhile wait for it.</summary>
    public void Load() => Enqueue(() =>
    {
        if (_loaded) return;

        var stored = JsonStore.Load(_file, () => new ClipHistoryFile());
        lock (_gate)
        {
            // Entries whose picture is gone are dropped rather than shown empty.
            _entries = [.. stored.Entries.Where(e => e.Kind != ClipKind.Image || File.Exists(ImagePath(e)))];
            _loaded = true;
        }

        Changed?.Invoke();
    });

    public void Add(ClipboardSnapshot snapshot) => Enqueue(() => Store(snapshot));

    /// <summary>After the limit was lowered in the options.</summary>
    public void Trim() => Enqueue(() =>
    {
        if (!_loaded) return;

        Prune();
        Save();
        Changed?.Invoke();
    });

    private void Enqueue(Action work)
    {
        lock (_gate)
        {
            _queue = _queue.ContinueWith(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    new ModuleLog("features").Warn($"Clipboard history: {ex.Message}");
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Whether a copy is kept at all; see the class remarks.</summary>
    private bool Wanted(ClipboardSnapshot snapshot, bool own)
    {
        var settings = _settings();

        // A paste from the panel is WinTo's own copy of an entry that is kept already.
        if (own) return true;
        if (settings.Paused) return false;

        if (snapshot.Source is { } source && settings.IgnoredApps.Any(app =>
                string.Equals(app.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase), source, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (snapshot.Files is not null) return settings.KeepFiles;
        if (snapshot.Text is { } text) return settings.KeepText && !MatchesIgnored(text, settings.IgnoredPatterns);
        return settings.KeepImages;
    }

    /// <summary>A pattern that doesn't parse or takes too long counts as no match.</summary>
    private static bool MatchesIgnored(string text, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;

            try
            {
                if (Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) return true;
            }
            catch (Exception)
            {
                // The options show which pattern is broken.
            }
        }

        return false;
    }

    private void Store(ClipboardSnapshot snapshot)
    {
        var own = string.Equals(snapshot.Source, OwnProcess, StringComparison.OrdinalIgnoreCase);
        if (!Wanted(snapshot, own)) return;

        var now = DateTime.Now;
        var record = new ClipRecord
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Copied = now,
            FirstCopied = now,
            Copies = 1,
            Source = own ? null : snapshot.Source,
        };
        byte[]? png = null;

        if (snapshot.Files is { } files)
        {
            record.Kind = ClipKind.Files;
            record.Files = [.. files];
            record.Hash = Hash("files", string.Join('\n', files));
        }
        else if (snapshot.Text is { } text)
        {
            record.Kind = ClipKind.Text;
            record.Text = text;
            record.Hash = Hash("text", text);
        }
        else
        {
            if (snapshot.Png is { } raw && ClipboardNative.PngSize(raw) is var (w, h))
            {
                png = raw;
                (record.Width, record.Height) = (w, h);
            }
            else if (snapshot.Dib is { } dib && ClipboardNative.DibToPng(dib) is var (converted, cw, ch))
            {
                png = converted;
                (record.Width, record.Height) = (cw, ch);
            }

            if (png is null) return;

            record.Kind = ClipKind.Image;
            record.Hash = Hash("image", Convert.ToHexString(SHA256.HashData(png)));
        }

        ClipRecord? existing = null;
        lock (_gate)
        {
            if (_entries.FindIndex(e => e.Hash == record.Hash) is var index and >= 0)
            {
                // Copied again: to the top and counted, keeping its pin and its files.
                existing = _entries[index];
                _entries.RemoveAt(index);
                existing.Copies = existing.CopyCount + 1;
                existing.FirstCopied ??= existing.Copied;
                existing.Copied = record.Copied;
                existing.Source = record.Source ?? existing.Source;
                _entries.Insert(0, existing);
                png = null;
            }
        }

        if (existing is not null)
        {
            // A new copy from a program brings its own formatting (or none); WinTo's paste brings what is stored.
            if (!own && existing.Kind == ClipKind.Text) existing.Rich = WriteRich(existing.Id, snapshot);
        }
        else if (png is not null)
        {
            Directory.CreateDirectory(ImageFolder);
            record.Image = record.Id + ".png";
            File.WriteAllBytes(Path.Combine(ImageFolder, record.Image), png);

            lock (_gate) _entries.Insert(0, record);
        }
        else if (record.Kind != ClipKind.Image)
        {
            if (record.Kind == ClipKind.Text) record.Rich = WriteRich(record.Id, snapshot);
            lock (_gate) _entries.Insert(0, record);
        }

        Prune();
        Save();
        Changed?.Invoke();
    }

    /// <summary>Stores or removes the formatting files of a text. Returns whether there is any.</summary>
    private bool WriteRich(string id, ClipboardSnapshot snapshot)
    {
        DeleteRich(id);
        if (snapshot.Html is not { Length: > 0 } && snapshot.Rtf is not { Length: > 0 }) return false;

        try
        {
            Directory.CreateDirectory(RichFolder);
            if (snapshot.Html is { Length: > 0 } html) File.WriteAllText(HtmlPath(id), html, new UTF8Encoding(false));
            if (snapshot.Rtf is { Length: > 0 } rtf) File.WriteAllBytes(RtfPath(id), rtf);
            return true;
        }
        catch (Exception)
        {
            DeleteRich(id);
            return false;
        }
    }

    /// <summary>Unpinned entries beyond the limits go, oldest first, with their pictures.</summary>
    private void Prune()
    {
        List<ClipRecord> removed;
        var max = MaxEntries;
        lock (_gate)
        {
            var unpinned = _entries.Where(e => !e.Pinned).ToList();
            removed = [.. unpinned.Skip(max)];
            removed.AddRange(unpinned.Take(max).Where(e => e.Kind == ClipKind.Image).Skip(MaxImages));
            _entries.RemoveAll(removed.Contains);
        }

        foreach (var record in removed) DeleteFiles(record);
    }

    public void Remove(string id)
    {
        ClipRecord? removed;
        lock (_gate)
        {
            removed = _entries.Find(e => e.Id == id);
            if (removed is not null) _entries.Remove(removed);
        }

        if (removed is null) return;

        DeleteFiles(removed);
        Save();
        Changed?.Invoke();
    }

    public void TogglePin(string id)
    {
        lock (_gate)
        {
            if (_entries.Find(e => e.Id == id) is not { } record) return;
            record.Pinned = !record.Pinned;
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes every unpinned entry. Returns how many.</summary>
    public int ClearUnpinned()
    {
        List<ClipRecord> removed;
        lock (_gate)
        {
            removed = [.. _entries.Where(e => !e.Pinned)];
            _entries.RemoveAll(e => !e.Pinned);
        }

        foreach (var record in removed) DeleteFiles(record);
        Save();
        Changed?.Invoke();
        return removed.Count;
    }

    private void DeleteFiles(ClipRecord record)
    {
        try
        {
            if (ImagePath(record) is { } path && File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A picture held open by a viewer stays until the next prune.
        }

        if (record.Rich) DeleteRich(record.Id);
    }

    private void DeleteRich(string id)
    {
        try
        {
            if (File.Exists(HtmlPath(id))) File.Delete(HtmlPath(id));
            if (File.Exists(RtfPath(id))) File.Delete(RtfPath(id));
        }
        catch (Exception)
        {
            // Left over, not harmful: nothing points to it.
        }
    }

    private void Save()
    {
        string json;
        lock (_gate)
        {
            if (!_loaded) return;
            json = JsonStore.Serialize(new ClipHistoryFile { Entries = _entries });
        }

        Directory.CreateDirectory(Folder);
        DeferredJson.Write(_file, json, ex => new ModuleLog("features").Warn($"Clipboard history not saved: {ex.Message}"));
    }

    private static string Hash(string kind, string content) =>
        kind + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..32];
}
