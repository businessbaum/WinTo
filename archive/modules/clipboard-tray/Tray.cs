using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MultiTool.Modules.ClipboardTray;

public enum EntryKind
{
    Text,
    Image,
    Files,
}

/// <summary>One item on the shelf.</summary>
/// <remarks>
/// <para>
/// <b>An image lives as a file, not in memory.</b> A dual-monitor screenshot is about 29 MB raw; ten
/// would be 290 MB that look like a leak. Only the thumbnail is kept; the full image is a PNG in
/// the data folder, read again when copying back.
/// </para>
/// <para>Text stays in memory; not worth the detour via disk.</para>
/// </remarks>
public sealed partial class TrayEntry : ObservableObject, IDisposable
{
    public required EntryKind Kind { get; init; }

    public required DateTimeOffset Added { get; init; }

    /// <summary>First line, file name or image size: the top line of the card.</summary>
    public required string Title { get; init; }

    /// <summary>Character count, size, number: the second line.</summary>
    public required string Detail { get; init; }

    /// <summary>Exact file size in bytes. Only for <see cref="EntryKind.Image"/>.</summary>
    public long SizeBytes { get; init; }

    /// <summary>Only for <see cref="EntryKind.Text"/>.</summary>
    public string Text { get; init; } = "";

    /// <summary>The PNG file in the data folder. Only for <see cref="EntryKind.Image"/>.</summary>
    public string? ImagePath { get; init; }

    /// <summary>Only for <see cref="EntryKind.Files"/>.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Scaled-down preview. Always for images, for files only if one of them is an image.</summary>
    public Bitmap? Thumbnail { get; init; }

    public bool IsText => Kind == EntryKind.Text;

    public bool IsImage => Kind == EntryKind.Image;

    public bool IsFiles => Kind == EntryKind.Files;

    public bool HasThumbnail => Thumbnail is not null;

    public string Time => Added.ToLocalTime().ToString("HH:mm");

    /// <summary>Set briefly after copying back; the card shows a check mark.</summary>
    [ObservableProperty] public partial bool JustCopied { get; set; }

    /// <summary>How two entries are recognized as the same.</summary>
    /// <remarks>Pasting twice without copying in between is a slip; this keeps the shelf from filling up with it.</remarks>
    public string Fingerprint => Kind switch
    {
        EntryKind.Text => "t:" + Text,
        EntryKind.Files => "f:" + string.Join('|', Files),

        // Image size and exact byte count, not the display text: Humanize.Bytes rounds, and two
        // different screenshots of the same resolution would collide.
        _ => $"i:{Title}:{SizeBytes}",
    };

    public void Dispose()
    {
        Thumbnail?.Dispose();

        // The PNG belongs to this entry only; keeping it would fill the data folder with every run.
        if (ImagePath is { Length: > 0 } path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Where the shelf's images live on disk.</summary>
/// <remarks>
/// Its own subfolder with only <c>.png</c> in it, so only files this module created are ever deleted.
/// <see cref="Prepare"/> clears a crash's leftovers on start; the shelf always starts empty.
/// </remarks>
internal sealed class TrayStore(string dataDirectory)
{
    private readonly string _folder = Path.Combine(dataDirectory, "shelf");

    public void Prepare()
    {
        Directory.CreateDirectory(_folder);
        Sweep();
    }

    /// <remarks>
    /// A random part after the time: two images in the same millisecond (two dropped at once) got the
    /// same name until 0.9.0, and the second overwrote the first.
    /// </remarks>
    public string NewImagePath() =>
        Path.Combine(_folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..28] + ".png");

    /// <summary>Deletes every PNG in its own subfolder.</summary>
    public void Sweep()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "*.png"))
            {
                try { File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
