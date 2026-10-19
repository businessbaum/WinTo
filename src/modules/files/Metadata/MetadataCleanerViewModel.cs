using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Files.Metadata;

/// <summary>What goes into the module's <c>settings.json</c>.</summary>
public sealed class CleanerSettings
{
    /// <summary>Whether the original is overwritten. Default is no.</summary>
    public bool ReplaceOriginal { get; set; }

    public bool NeutralName { get; set; } = true;
    public bool ResetFileTimes { get; set; } = true;
    public string Suffix { get; set; } = " (no metadata)";
}

public sealed partial class FileRow : ObservableObject
{
    public FileRow(string path)
    {
        Path = path;
        Kind = Cleaner.KindOf(path);
        Name = System.IO.Path.GetFileName(path);

        try
        {
            SizeText = Humanize.Bytes(new FileInfo(path).Length);
        }
        catch (Exception)
        {
            SizeText = "";
        }
    }

    public string Path { get; }

    public string Name { get; }

    public FileKind Kind { get; }

    public string SizeText { get; }

    public string KindText => Kind switch
    {
        FileKind.Jpeg => "JPEG",
        FileKind.Png => "PNG",
        FileKind.Media => "Audio / video",
        FileKind.WebP => "WebP",
        FileKind.Gif => "GIF",
        FileKind.Raster => "PNG export",
        FileKind.Pdf => "PDF page images",
        FileKind.Office => "Office",
        _ => "not supported",
    };

    [ObservableProperty] public partial string? OutputPath { get; set; }
    [ObservableProperty] public partial string? Verification { get; set; }

    public bool IsSupported => Kind != FileKind.Unknown;

    [ObservableProperty] public partial Inspection? Inspection { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial bool IsDone { get; set; }
    [ObservableProperty] public partial bool IsFailed { get; set; }

    /// <summary>Whether this result deserves a warning: GPS, names, timestamps.</summary>
    public bool HasSensitive => Inspection?.HasSensitive == true;

    partial void OnInspectionChanged(Inspection? value)
    {
        OnPropertyChanged(nameof(HasSensitive));

        StatusText = value is null ? ""
            : value.Problem is not null ? value.Problem
            : value.Entries.Count == 0 ? "no removable container data found"
            : value.HasSensitive ? $"{value.Entries.Count} entries, some sensitive"
            : $"{value.Entries.Count} entries";
    }
}

public sealed partial class MetadataCleanerViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private CancellationTokenSource? _cancellation;
    private readonly CancellationTokenSource _lifetime;
    private bool _disposed;

    // Set until the end of the constructor: the assignments below fire On…Changed, which called
    // Save() while the other property still had its default (the "settings.json half empty" trap).
    private readonly bool _loading = true;

    public MetadataCleanerViewModel(IModuleContext context)
    {
        _context = context;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.Shutdown);

        var settings = context.Settings.Load<CleanerSettings>();
        ReplaceOriginal = settings.ReplaceOriginal;
        NeutralName = settings.NeutralName;
        ResetFileTimes = settings.ResetFileTimes;
        Suffix = settings.Suffix;

        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFiles));
            OnPropertyChanged(nameof(CountText));
        };

        _loading = false;
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<FileRow> Files { get; } = [];

    public bool HasFiles => Files.Count > 0;

    public string CountText => Files.Count switch
    {
        0 => "No files",
        1 => "1 file",
        _ => $"{Files.Count} files",
    };

    [ObservableProperty] public partial FileRow? Selected { get; set; }
    [ObservableProperty] public partial bool NeutralName { get; set; } = true;
    [ObservableProperty] public partial bool ResetFileTimes { get; set; } = true;
    [ObservableProperty] public partial bool ReplaceOriginal { get; set; }
    [ObservableProperty] public partial string Suffix { get; set; } = " (no metadata)";
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    /// <summary>The entries of the selected file, sorted by group.</summary>
    public ObservableCollection<MetadataEntry> Details { get; } = [];

    public bool HasDetails => Details.Count > 0;

    partial void OnSelectedChanged(FileRow? value)
    {
        Details.Clear();

        if (value?.Inspection is null)
        {
            OnPropertyChanged(nameof(HasDetails));
            return;
        }

        foreach (var entry in value.Inspection.Entries
                     .OrderByDescending(e => e.Sensitive)
                     .ThenBy(e => e.Group, StringComparer.Ordinal))
        {
            Details.Add(entry);
        }

        OnPropertyChanged(nameof(HasDetails));
    }

    partial void OnNeutralNameChanged(bool value) => Save();
    partial void OnResetFileTimesChanged(bool value) => Save();
    partial void OnReplaceOriginalChanged(bool value) => Save();
    partial void OnSuffixChanged(string value) => Save();

    private void Save()
    {
        if (_loading || _disposed) return;

        _context.Settings.Save(new CleanerSettings
        {
            ReplaceOriginal = ReplaceOriginal,
            NeutralName = NeutralName,
            ResetFileTimes = ResetFileTimes,
            Suffix = Suffix,
        });
    }

    // ------------------------------------------------------------------ Files

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        if (IsWorking || _disposed) return;
        var picked = await _context.Shell.PickFilesAsync("Choose files", multiple: true,
            new FilePattern("Images, media and documents",
                "jpg", "jpeg", "png", "heic", "webp", "tif", "tiff", "gif", "avif", "bmp", "pdf", "docx", "xlsx", "pptx",
                "mp4", "mov", "mkv", "webm", "avi", "mp3", "m4a", "flac"),
            new FilePattern("All files", "*"));

        await AddAsync(picked);
    }

    /// <summary>Also used when dragging from Explorer.</summary>
    public async Task AddAsync(IEnumerable<string> paths)
    {
        if (IsWorking || _disposed) return;
        var added = new List<FileRow>();

        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            if (Files.Any(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;

            var row = new FileRow(path);
            Files.Add(row);
            added.Add(row);
        }

        Selected ??= added.FirstOrDefault();

        IsWorking = true;
        try { await InspectAsync(added); }
        finally { IsWorking = false; }
    }

    /// <summary>Reads the metadata of the new files.</summary>
    /// <remarks>
    /// One after another: for a video that's an ffprobe call, and fifty processes at once stall the
    /// machine for seconds without answering sooner.
    /// </remarks>
    private async Task InspectAsync(IReadOnlyList<FileRow> rows)
    {
        if (rows.Count == 0) return;

        var ffprobe = _context.Tools.Find("ffprobe");

        foreach (var row in rows)
        {
            if (!row.IsSupported)
            {
                row.StatusText = "not supported";
                continue;
            }

            try
            {
                row.Inspection = await Cleaner.InspectAsync(row.Path, ffprobe, _lifetime.Token);

                // Otherwise the details of the already selected row stay empty until someone clicks away and back.
                if (ReferenceEquals(row, Selected)) OnSelectedChanged(row);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    [RelayCommand]
    private void Remove(FileRow? row)
    {
        if (row is null || IsWorking || _disposed) return;

        if (ReferenceEquals(row, Selected)) Selected = null;
        Files.Remove(row);
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsWorking || _disposed) return;
        Selected = null;
        Files.Clear();
        Message = null;
    }

    // ------------------------------------------------------------------ clean

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsWorking || _disposed) return;
        var todo = Files.Where(f => f.IsSupported && !f.IsDone).ToArray();
        if (todo.Length == 0) return;
        // Freeze all choices before the first await, including confirmation.
        var options = new CleanOptions(ReplaceOriginal, NeutralName, ResetFileTimes, Suffix);
        IsWorking = true;
        Message = null;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _cancellation.Token;
        int done = 0, failed = 0;
        try
        {
            string changes = string.Join("\n\n", todo.Select(f => f.Kind).Distinct().Select(kind => kind switch
            {
                FileKind.Pdf => "PDF: pages are rebuilt as images at 216 dpi; text search, links, forms, signatures and vector graphics are lost.",
                FileKind.Office => "Office: comments, tracked changes, speaker notes, embedded objects and external links are removed; changes are accepted.",
                FileKind.Raster => "Other image formats: only the first image is exported as PNG.",
                FileKind.Media => "Media: extra tracks, chapters and known SEI side data are dropped; this can remove subtitles and dynamic HDR.",
                _ => "Images: color profiles and resolution info are dropped; colors may change.",
            }));
            if (!await _context.Shell.ConfirmAsync("Start cleaning", changes + (options.ReplaceOriginal ? "\n\nOriginals go to the recycle bin after a successful check and are replaced." : "\n\nCleaned copies are saved next to them."), "Clean", danger: options.ReplaceOriginal)) return;
            token.ThrowIfCancellationRequested();
            var ffmpeg = _context.Tools.Find("ffmpeg");
            var ffprobe = _context.Tools.Find("ffprobe");
            foreach (var row in todo)
            {
                token.ThrowIfCancellationRequested();
                ActivityChanged?.Invoke(this, new ModuleActivity(true, $"Cleaning {row.Name}", (double)(done + failed) / todo.Length));
                row.IsFailed = false;
                var result = await Cleaner.CleanToFileAsync(row.Path, options, ffmpeg, ffprobe, token);
                if (_disposed) return;
                row.IsDone = result.Ok;
                row.IsFailed = !result.Ok;
                row.OutputPath = result.OutputPath;
                row.Verification = result.Verification;
                row.StatusText = result.Ok ? "cleaned and verified" : result.Problem ?? "Failed";
                if (result.Ok) done++; else failed++;
            }
            Message = $"{done} files cleaned, {failed} failed.";
            MessageIsError = failed > 0;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) { Message = $"Cancelled after {done} cleaned files."; MessageIsError = false; }
        }
        catch (Exception ex)
        {
            if (!_disposed) { Message = ex.Message; MessageIsError = true; }
        }
        finally
        {
            IsWorking = false;
            _cancellation.Dispose();
            _cancellation = null;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        ActivityChanged = null;
    }
    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void Reveal(FileRow? row)
    {
        if (row is not null) _context.Shell.RevealInExplorer(row.OutputPath ?? row.Path);
    }
}
