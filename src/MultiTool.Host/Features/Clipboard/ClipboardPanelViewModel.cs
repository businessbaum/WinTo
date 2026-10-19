using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Host.Services;

namespace MultiTool.Host.Features.Clipboard;

/// <summary>The list behind the clipboard panel: search, select, paste, pin, remove, pause.</summary>
/// <remarks>
/// Since 0.13.0 with what Maccy offers on the Mac: exact, fuzzy or regex search, an order (last copied, first
/// copied, most copied), Ctrl+1 to Ctrl+9 for the first nine rows, pasting with or without formatting, a pause.
/// </remarks>
public sealed partial class ClipboardPanelViewModel : ObservableObject, IDisposable
{
    private const int Shown = 80;

    private readonly ClipboardHistory _history;
    private readonly ClipboardSettings _settings;
    private readonly Action _settingsChanged;
    private readonly Dictionary<string, ClipItem> _items = [];
    private bool _disposed;

    internal ClipboardPanelViewModel(ClipboardHistory history, ClipboardSettings settings, Action settingsChanged)
    {
        _history = history;
        _settings = settings;
        _settingsChanged = settingsChanged;
        _history.Changed += OnHistoryChanged;
        Paused = settings.Paused;
        Refresh();
    }

    /// <summary>An entry was chosen; the panel closes and the feature pastes it. The flag asks for plain text.</summary>
    public event Action<ClipRecord, bool>? PasteRequested;

    public ObservableCollection<ClipItem> Items { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial ClipItem? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string EmptyText { get; set; } = "";

    /// <summary>New copies are not kept; stored in the settings, so it holds until resumed.</summary>
    [ObservableProperty]
    public partial bool Paused { get; set; }

    public string SearchHint => _settings.Search switch
    {
        ClipSearch.Exact => "Search the clipboard history",
        ClipSearch.Regex => "Search with a regular expression",
        _ => "Search the clipboard history (fuzzy)",
    };

    public string KeysHint => _settings.KeepFormatting
        ? "Enter pastes · Shift+Enter plain · Ctrl+1–9 · Ctrl+P pins · Del removes"
        : "Enter pastes plain · Shift+Enter formatted · Ctrl+1–9 · Ctrl+P pins · Del removes";

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnPausedChanged(bool value)
    {
        if (_settings.Paused == value) return;

        _settings.Paused = value;
        _settingsChanged();
    }

    [RelayCommand]
    private void TogglePause() => Paused = !Paused;

    private void OnHistoryChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) Refresh();
    });

    private void Refresh()
    {
        var query = Query.Trim();
        var selectedId = Selected?.Record.Id;
        var (matching, problem) = Find(_history.Entries, query);

        Items.Clear();
        for (var i = 0; i < matching.Count; i++)
        {
            var record = matching[i];
            if (!_items.TryGetValue(record.Id, out var item))
            {
                item = new ClipItem(record, _history.ImagePath(record));
                _items[record.Id] = item;
            }

            item.Update(record, i < 9 ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            Items.Add(item);
        }

        // Thumbnails of entries no longer listed are released.
        foreach (var gone in _items.Keys.Except(matching.Select(r => r.Id)).ToList())
        {
            _items[gone].Dispose();
            _items.Remove(gone);
        }

        Selected = Items.FirstOrDefault(i => i.Record.Id == selectedId) ?? Items.FirstOrDefault();
        IsEmpty = Items.Count == 0;
        EmptyText = problem ?? (query.Length > 0 ? "No entry matches." : "Nothing copied yet.");
    }

    /// <summary>Pinned first, then the chosen order; a fuzzy search puts exact hits before letter-order hits.</summary>
    private (List<ClipRecord> Records, string? Problem) Find(IReadOnlyList<ClipRecord> entries, string query)
    {
        IEnumerable<ClipRecord> ordered = _settings.Order switch
        {
            ClipOrder.FirstCopied => entries.OrderByDescending(e => e.FirstCopy),
            ClipOrder.MostCopied => entries.OrderByDescending(e => e.CopyCount).ThenByDescending(e => e.Copied),
            _ => entries,
        };
        ordered = ordered.OrderByDescending(e => e.Pinned);

        if (query.Length == 0) return ([.. ordered.Take(Shown)], null);

        switch (_settings.Search)
        {
            case ClipSearch.Regex:
                Regex pattern;
                try
                {
                    pattern = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    return ([], "Not a valid regular expression.");
                }

                return ([.. ordered.Where(e => SearchText(e).Any(t => SafeMatch(pattern, t))).Take(Shown)], null);

            case ClipSearch.Exact:
                return ([.. ordered.Where(e => SearchText(e).Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase))).Take(Shown)], null);

            default:
                var exact = ordered.Where(e => SearchText(e).Any(t => t.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
                var fuzzy = ordered.Except(exact)
                    .Select(e => (Entry: e, Score: SearchText(e).Select(t => FuzzyScore(t, query)).DefaultIfEmpty(-1).Max()))
                    .Where(m => m.Score >= 0)
                    .OrderBy(m => m.Score)
                    .Select(m => m.Entry);
                return ([.. exact.Concat(fuzzy).Take(Shown)], null);
        }
    }

    private static bool SafeMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>What a search looks at: the text, the file paths, the source program, "image".</summary>
    private static IEnumerable<string> SearchText(ClipRecord record)
    {
        if (record.Text is { } text) yield return text.Length > 20_000 ? text[..20_000] : text;
        if (record.Files is { } files) foreach (var file in files) yield return file;
        if (record.Source is { } source) yield return source;
        if (record.Kind == ClipKind.Image) yield return "image";
    }

    /// <summary>The letters of <paramref name="query"/> in order within <paramref name="text"/>, ignoring case.</summary>
    /// <returns>-1 for no match, otherwise the span they cover: smaller is closer.</returns>
    private static int FuzzyScore(string text, string query)
    {
        var start = -1;
        var position = 0;
        foreach (var c in query)
        {
            if (char.IsWhiteSpace(c)) continue;

            var found = text.IndexOf(c.ToString(), position, StringComparison.CurrentCultureIgnoreCase);
            if (found < 0) return -1;
            if (start < 0) start = found;
            position = found + 1;
        }

        return start < 0 ? -1 : position - start;
    }

    public void Move(int delta)
    {
        if (Items.Count == 0) return;

        var index = Selected is null ? -1 : Items.IndexOf(Selected);
        Selected = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    [RelayCommand]
    public void Paste(ClipItem? item) => PasteItem(item, alternate: false);

    /// <param name="alternate">Shift held: the other way round from the "keep formatting" setting.</param>
    public void PasteItem(ClipItem? item, bool alternate)
    {
        if ((item ?? Selected) is { } chosen) PasteRequested?.Invoke(chosen.Record, _settings.KeepFormatting == alternate);
    }

    /// <summary>Ctrl+1 to Ctrl+9: the row with that number.</summary>
    public void PasteNumber(int number, bool alternate)
    {
        if (number >= 1 && number <= Math.Min(9, Items.Count)) PasteItem(Items[number - 1], alternate);
    }

    [RelayCommand]
    public void TogglePin(ClipItem? item)
    {
        if ((item ?? Selected) is { } chosen) _history.TogglePin(chosen.Record.Id);
    }

    [RelayCommand]
    public void Remove(ClipItem? item)
    {
        if ((item ?? Selected) is not { } chosen) return;

        // The next one down takes the selection, so Delete can be pressed repeatedly.
        var index = Items.IndexOf(chosen);
        if (index + 1 < Items.Count) Selected = Items[index + 1];
        else if (index > 0) Selected = Items[index - 1];

        _history.Remove(chosen.Record.Id);
    }

    public void Dispose()
    {
        _disposed = true;
        _history.Changed -= OnHistoryChanged;
        foreach (var item in _items.Values) item.Dispose();
        _items.Clear();
        Items.Clear();
    }
}

/// <summary>One row of the panel.</summary>
public sealed partial class ClipItem : ObservableObject, IDisposable
{
    private readonly string? _imagePath;
    private bool _thumbnailRequested;
    private bool _disposed;

    internal ClipItem(ClipRecord record, string? imagePath)
    {
        Record = record;
        _imagePath = imagePath;
    }

    public ClipRecord Record { get; private set; }

    public bool IsText => Record.Kind == ClipKind.Text;
    public bool IsImage => Record.Kind == ClipKind.Image;
    public bool IsFiles => Record.Kind == ClipKind.Files;

    [ObservableProperty]
    public partial bool Pinned { get; set; }

    [ObservableProperty]
    public partial string Meta { get; set; } = "";

    /// <summary>"1" to "9" for the first rows (Ctrl+number pastes), empty below.</summary>
    [ObservableProperty]
    public partial string Number { get; set; } = "";

    /// <summary>The tooltip: the whole text (up to a limit), then when, how often and from where.</summary>
    [ObservableProperty]
    public partial string Details { get; set; } = "";

    /// <summary>The first lines of a text, file names, or nothing for an image.</summary>
    public string Preview => Record.Kind switch
    {
        ClipKind.Text => Shorten(Record.Text ?? ""),
        ClipKind.Files => string.Join("\n", (Record.Files ?? []).Take(3).Select(Path.GetFileName))
                          + ((Record.Files?.Count ?? 0) > 3 ? $"\n+ {Record.Files!.Count - 3} more" : ""),
        _ => "",
    };

    /// <summary>Decoded small and on first use: most rows are never scrolled to.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested && IsImage && _imagePath is { } path)
            {
                _thumbnailRequested = true;
                _ = LoadThumbnailAsync(path);
            }

            return field;
        }
        private set => SetProperty(ref field, value);
    }

    internal void Update(ClipRecord record, string number)
    {
        Record = record;
        Pinned = record.Pinned;
        Number = number;

        var kind = record.Kind switch
        {
            ClipKind.Image => $"Image · {record.Width} × {record.Height}",
            ClipKind.Files => record.Files?.Count == 1 ? "File" : $"{record.Files?.Count ?? 0} files",
            _ => $"{(record.Text?.Length ?? 0):N0} characters" + (record.Rich ? " · formatted" : ""),
        };

        var copies = record.CopyCount > 1 ? $"{record.CopyCount}×" : null;
        Meta = string.Join(" · ", new[] { kind, record.Source, copies, Humanize.Ago(record.Copied) }.Where(p => !string.IsNullOrEmpty(p)));

        var body = record.Kind switch
        {
            ClipKind.Text => Limit(record.Text ?? ""),
            ClipKind.Files => string.Join("\n", (record.Files ?? []).Take(30)) + ((record.Files?.Count ?? 0) > 30 ? $"\n+ {record.Files!.Count - 30} more" : ""),
            _ => $"Image, {record.Width} × {record.Height} pixels",
        };

        var when = record.CopyCount > 1
            ? $"Copied {record.CopyCount} times · first {Humanize.Ago(record.FirstCopy)} · last {Humanize.Ago(record.Copied)}"
            : $"Copied {Humanize.Ago(record.Copied)}";
        Details = $"{body}\n\n{when}" + (record.Source is { } source ? $" · from {source}" : "");
    }

    /// <summary>At most 40 lines and 3000 characters: a tooltip, not a reader.</summary>
    private static string Limit(string text)
    {
        var lines = text.Split('\n');
        var shown = string.Join("\n", lines.Take(40).Select(l => l.TrimEnd('\r')));
        if (shown.Length > 3000) shown = shown[..3000];
        return shown.Length < text.Length - (lines.Length - 1) ? shown + "\n…" : shown;
    }

    private async Task LoadThumbnailAsync(string path)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToHeight(stream, 120, BitmapInterpolationMode.MediumQuality);
            });

            if (_disposed) bitmap.Dispose();
            else Thumbnail = bitmap;
        }
        catch (Exception)
        {
            // Without a picture the row still shows its size.
        }
    }

    /// <summary>Whitespace collapsed at the start, at most four lines of 200 characters.</summary>
    private static string Shorten(string text)
    {
        var lines = text.TrimStart().Split('\n').Take(4).Select(l => l.TrimEnd('\r'));
        var joined = string.Join("\n", lines.Select(l => l.Length > 200 ? l[..200] + "…" : l));
        return joined.Length < text.TrimStart().Length && !joined.EndsWith('…') ? joined + " …" : joined;
    }

    public void Dispose()
    {
        _disposed = true;
        _thumbnailRequested = true;
        var thumbnail = Thumbnail;
        Thumbnail = null;
        thumbnail?.Dispose();
    }
}
