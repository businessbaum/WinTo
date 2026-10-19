using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO.Compression;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.ClipboardTray;

public sealed partial class ClipboardTrayViewModel : ObservableObject, IDisposable
{
    /// <summary>Preview width in pixels; enough for the card even at 150 % scaling.</summary>
    private const int ThumbnailWidth = 520;

    /// <summary>Shelf limit.</summary>
    /// <remarks>
    /// A shelf without limit is an archive, and an archive would have to be saved. Beyond forty cards
    /// the oldest drops out, with its file.
    /// </remarks>
    private const int MaxEntries = 40;

    private static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    private readonly IModuleContext _context;
    private readonly TrayStore _store;

    public ClipboardTrayViewModel(IModuleContext context)
    {
        _context = context;
        _store = new TrayStore(context.DataDirectory);
        _store.Prepare();

        // A binding on .Count does nothing in Avalonia (no int to bool); the properties below do it
        // explicitly.
        Entries.CollectionChanged += OnEntriesChanged;
    }

    /// <summary>Set by the view once it is attached to a window.</summary>
    public IClipboard? Clipboard { get; set; }

    /// <summary>Also from the view. Needed to put files back into the clipboard.</summary>
    public IStorageProvider? Storage { get; set; }

    /// <summary>Newest first: what was just added is at the top left.</summary>
    public ObservableCollection<TrayEntry> Entries { get; } = [];

    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty] public partial bool MessageIsError { get; set; }

    /// <summary>The entry shown large. <c>null</c> if none.</summary>
    [ObservableProperty] public partial TrayEntry? Preview { get; set; }

    /// <summary>The full image for the preview.</summary>
    /// <remarks>Not the thumbnail: scaled down to card width, it would be unreadable when enlarged.</remarks>
    [ObservableProperty] public partial Bitmap? PreviewImage { get; set; }

    public bool HasPreview => Preview is not null;

    partial void OnPreviewChanged(TrayEntry? value) => OnPropertyChanged(nameof(HasPreview));

    public bool HasEntries => Entries.Count > 0;

    public bool IsEmpty => Entries.Count == 0;

    public string Summary => Entries.Count switch
    {
        0 => "Empty",
        1 => "1 item",
        _ => $"{Entries.Count} items",
    };

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    // ================================================================= Adding

    /// <summary>Takes whatever is in the clipboard.</summary>
    /// <remarks>
    /// <b>The query order is the answer.</b> A folder copied in Explorer is there as a file list <i>and</i>
    /// as text; an image copied in a browser as an image and a URL. Asking for text first gets the
    /// weaker half in both cases.
    /// </remarks>
    [RelayCommand]
    private async Task PasteAsync()
    {
        if (Clipboard is null) return;

        try
        {
            var files = await Clipboard.TryGetFilesAsync();
            if (files is { Length: > 0 })
            {
                await AddFilesAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
                return;
            }

            var bitmap = await Clipboard.TryGetBitmapAsync();
            if (bitmap is not null)
            {
                await AddBitmapAsync(bitmap);
                return;
            }

            var text = await Clipboard.TryGetTextAsync();
            if (text is { Length: > 0 })
            {
                Add(BuildText(text));
                return;
            }

            Show("Nothing in the clipboard.", error: true);
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    /// <summary>From the view when text is dropped.</summary>
    public void AddText(string text)
    {
        if (text.Length == 0) return;
        Add(BuildText(text));
    }

    /// <summary>From the view when an image is dropped.</summary>
    public async Task AddBitmapAsync(Bitmap bitmap)
    {
        try
        {
            var path = _store.NewImagePath();
            var size = bitmap.PixelSize;

            await Task.Run(() =>
            {
                using (bitmap)
                {
                    // Fastest level instead of smallest file: the PNG sits in the data folder for minutes and
                    // is read once. For a dual-monitor screenshot that's a blink versus a second.
                    bitmap.Save(path, new PngBitmapEncoderOptions
                    {
                        CompressionLevel = CompressionLevel.Fastest,
                    });
                }
            });

            var bytes = new FileInfo(path).Length;

            // Like the file branch below: reading and decoding stay off the UI thread.
            var thumbnail = await Task.Run(() => LoadThumbnail(path));

            Add(new TrayEntry
            {
                Kind = EntryKind.Image,
                Added = DateTimeOffset.Now,
                Title = $"{size.Width} × {size.Height}",
                Detail = Humanize.Bytes(bytes),
                SizeBytes = bytes,
                ImagePath = path,
                Thumbnail = thumbnail,
            });
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    /// <summary>From the view when files are dropped.</summary>
    public async Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        try
        {
            // A single image file gets a preview. The file itself is still what gets copied: whoever
            // drops a file wants to paste it in Explorer later.
            Bitmap? thumbnail = null;
            if (paths.Count == 1 && ImageExtensions.Contains(Path.GetExtension(paths[0]), StringComparer.OrdinalIgnoreCase))
                thumbnail = await Task.Run(() => LoadThumbnail(paths[0]));

            var total = paths.Sum(p =>
            {
                try { return new FileInfo(p).Length; }
                catch (IOException) { return 0L; }
                catch (UnauthorizedAccessException) { return 0L; }
            });

            Add(new TrayEntry
            {
                Kind = EntryKind.Files,
                Added = DateTimeOffset.Now,
                Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} files",
                Detail = paths.Count == 1 ? Humanize.Bytes(total) : $"{Humanize.Bytes(total)} total",
                Files = paths,
                Thumbnail = thumbnail,
            });
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    private static TrayEntry BuildText(string text)
    {
        var firstLine = text.Split('\n', 2)[0].TrimEnd('\r').Trim();
        var lines = text.Replace("\r\n", "\n").Split('\n').Length;

        return new TrayEntry
        {
            Kind = EntryKind.Text,
            Added = DateTimeOffset.Now,
            Title = firstLine.Length > 0 ? firstLine : "Whitespace",
            Detail = lines > 1 ? $"{text.Length} characters · {lines} lines" : $"{text.Length} characters",
            Text = text,
        };
    }

    private static Bitmap? LoadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, ThumbnailWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            // The card is still usable without a preview; an error message would be louder than the damage.
            return null;
        }
    }

    private void Add(TrayEntry entry)
    {
        if (Entries.FirstOrDefault() is { } newest && newest.Fingerprint == entry.Fingerprint)
        {
            entry.Dispose();
            Show("Already at the top of the shelf.");
            return;
        }

        Entries.Insert(0, entry);

        while (Entries.Count > MaxEntries)
        {
            var oldest = Entries[^1];
            Entries.RemoveAt(Entries.Count - 1);
            if (Preview == oldest) ClosePreview();
            oldest.Dispose();
        }

        Show($"Added: {entry.Title}");
    }

    // ================================================================= Viewing

    /// <summary>Shows an entry large.</summary>
    /// <remarks>
    /// A screenshot on a 248 px card only shows that it is a screenshot. Searching the shelf needs a
    /// proper look, or one copies them one by one.
    /// </remarks>
    public async Task ShowPreviewAsync(TrayEntry entry)
    {
        ClosePreview();
        Preview = entry;

        if (entry.Kind is not EntryKind.Image || entry.ImagePath is not { Length: > 0 } path) return;

        try
        {
            var full = await Task.Run(() => new Bitmap(path));

            // Between starting to load and here, the preview may have closed or switched entries.
            if (Preview != entry)
            {
                full.Dispose();
                return;
            }

            PreviewImage = full;
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    /// <summary>Copies the shown entry and closes the preview.</summary>
    /// <remarks>
    /// Closing is part of it: the feedback is a check mark on the card, which sits under the preview.
    /// </remarks>
    [RelayCommand]
    private async Task CopyPreviewAsync()
    {
        if (Preview is not { } entry) return;

        ClosePreview();
        await CopyBackAsync(entry);
    }

    [RelayCommand]
    public void ClosePreview()
    {
        Preview = null;

        PreviewImage?.Dispose();
        PreviewImage = null;
    }

    // ================================================================= Copying back

    [RelayCommand]
    public async Task CopyBackAsync(TrayEntry? entry)
    {
        if (entry is null || Clipboard is null) return;

        try
        {
            switch (entry.Kind)
            {
                case EntryKind.Text:
                    await Clipboard.SetTextAsync(entry.Text);
                    break;

                case EntryKind.Image when entry.ImagePath is { Length: > 0 } path:
                    // Only here is the full image read again; the preview is scaled down to card width.
                    using (var bitmap = await Task.Run(() => new Bitmap(path)))
                    {
                        await Clipboard.SetBitmapAsync(bitmap);
                    }
                    break;

                case EntryKind.Files when Storage is not null:
                    var items = new List<IStorageItem>();
                    foreach (var file in entry.Files)
                    {
                        if (await Storage.TryGetFileFromPathAsync(new Uri(file)) is { } item)
                            items.Add(item);
                    }

                    if (items.Count == 0)
                    {
                        Show("The file is no longer there.", error: true);
                        return;
                    }

                    await Clipboard.SetFilesAsync(items);
                    break;

                default:
                    return;
            }

            await FlashAsync(entry);
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
    }

    /// <summary>The check mark on the card.</summary>
    /// <remarks>Copying back changes nothing visible; without feedback one clicks again.</remarks>
    private async Task FlashAsync(TrayEntry entry)
    {
        entry.JustCopied = true;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1.2), _context.Shutdown);
        }
        catch (OperationCanceledException)
        {
        }

        entry.JustCopied = false;
    }

    // ================================================================= Removing

    [RelayCommand]
    private void Remove(TrayEntry? entry)
    {
        if (entry is null) return;

        // Preview first, then Dispose: otherwise it points at a released bitmap and a deleted PNG.
        if (Preview == entry) ClosePreview();

        Entries.Remove(entry);
        entry.Dispose();
        Message = null;
    }

    [RelayCommand]
    private void ClearAll()
    {
        ClosePreview();

        foreach (var entry in Entries) entry.Dispose();

        Entries.Clear();
        _store.Sweep();
        Show("Shelf cleared.");
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    public void Dispose()
    {
        ClosePreview();

        foreach (var entry in Entries) entry.Dispose();

        Entries.Clear();

        // Whatever remains after a crash is removed by Prepare() on the next start.
        _store.Sweep();
    }
}
