using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Files.Convert;

public enum JobState
{
    Ready,
    Running,
    Done,
    Failed,
}

/// <summary>An image in the list.</summary>
public sealed partial class ImageJob : ObservableObject
{
    public ImageJob(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        IsVideo = Converter.LooksLikeVideo(path);

        try { BeforeBytes = new FileInfo(path).Length; }
        catch (Exception) { BeforeBytes = 0; }
    }

    public string Path { get; }

    public string Name { get; }

    public long BeforeBytes { get; }

    /// <summary>Videos are remuxed, not re-encoded.</summary>
    public bool IsVideo { get; }

    [ObservableProperty] public partial JobState State { get; set; } = JobState.Ready;

    [ObservableProperty] public partial string? OutputPath { get; set; }

    [ObservableProperty] public partial string StatusText { get; set; } = "";

    /// <summary>The state stamp.</summary>
    /// <remarks>
    /// "ready", not "waiting": in the YouTube module "waiting" once sat above an indeterminate progress
    /// bar and read as if something was happening. What hasn't started shouldn't look like it.
    /// </remarks>
    public string StateText => State switch
    {
        JobState.Running => "running",
        JobState.Done => "done",
        JobState.Failed => "error",
        _ => "ready",
    };

    public bool IsDone => State == JobState.Done;

    public bool IsFailed => State == JobState.Failed;

    public bool IsRunning => State == JobState.Running;

    public string SizeText { get; private set; } = "";

    public void Apply(ConvertResult result)
    {
        State = result.Ok ? JobState.Done : JobState.Failed;
        OutputPath = result.OutputPath;

        if (result.Ok)
        {
            var change = BeforeBytes == 0 ? 0 : 100.0 * (result.AfterBytes - BeforeBytes) / BeforeBytes;

            SizeText = $"{Humanize.Bytes(BeforeBytes)} → {Humanize.Bytes(result.AfterBytes)}"
                     + $"   ({change:+0;-0;0} %)";

            StatusText = $"{result.Width} × {result.Height}"
                       + (result.UsedFfmpeg ? "  ·  read via ffmpeg" : "");
        }
        else
        {
            SizeText = "";
            StatusText = result.Problem ?? "Unknown error.";
        }

        OnPropertyChanged(nameof(SizeText));
    }

    partial void OnStateChanged(JobState value)
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsRunning));
    }
}

/// <summary>What is kept between sessions.</summary>
public sealed class ImageConvertSettings
{
    public int Format { get; set; }
    public int Quality { get; set; } = 85;
    public int Resize { get; set; }
    public int MaxEdge { get; set; } = 1920;
    public int Percent { get; set; } = 50;
    public string? TargetDirectory { get; set; }
    public bool Overwrite { get; set; }

    /// <summary>0 MP4 · 1 MKV.</summary>
    public int Video { get; set; }
}

public sealed partial class ImageConvertViewModel : ObservableObject
{
    private readonly IModuleContext _context;
    private CancellationTokenSource? _run;
    private bool _loading = true;

    public ImageConvertViewModel(IModuleContext context)
    {
        _context = context;

        var stored = context.Settings.Load<ImageConvertSettings>();

        Format = stored.Format;
        Quality = stored.Quality;
        Resize = stored.Resize;
        MaxEdge = stored.MaxEdge;
        Percent = stored.Percent;
        TargetDirectory = stored.TargetDirectory;
        Overwrite = stored.Overwrite;
        Video = stored.Video;

        Jobs.CollectionChanged += (_, _) => RefreshQueue();

        _loading = false;
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<ImageJob> Jobs { get; } = [];

    // --------------------------------------------------------------- Settings

    /// <summary>0 PNG · 1 JPEG · 2 WebP.</summary>
    [ObservableProperty] public partial int Format { get; set; }

    [ObservableProperty] public partial int Quality { get; set; } = 85;

    /// <summary>0 unchanged · 1 longest edge · 2 share.</summary>
    [ObservableProperty] public partial int Resize { get; set; }

    [ObservableProperty] public partial int MaxEdge { get; set; } = 1920;

    [ObservableProperty] public partial int Percent { get; set; } = 50;

    [ObservableProperty] public partial string? TargetDirectory { get; set; }

    [ObservableProperty] public partial bool Overwrite { get; set; }

    /// <summary>0 MP4 · 1 MKV.</summary>
    [ObservableProperty] public partial int Video { get; set; }

    public bool VideoIsMp4 => Video == 0;
    public bool VideoIsMkv => Video == 1;

    /// <summary>Whether any video is in the list.</summary>
    public bool HasVideos => Jobs.Any(j => j.IsVideo);

    [RelayCommand]
    private void SelectVideo(string index)
    {
        if (int.TryParse(index, out var value)) Video = value;
    }

    partial void OnVideoChanged(int value)
    {
        OnPropertyChanged(nameof(VideoIsMp4));
        OnPropertyChanged(nameof(VideoIsMkv));
        Save();
    }

    public bool IsPng => Format == 0;
    public bool IsJpeg => Format == 1;
    public bool IsWebP => Format == 2;

    /// <summary>PNG is lossless; a slider for it would be a dummy.</summary>
    public bool HasQuality => Format != 0;

    public bool ResizeIsNone => Resize == 0;
    public bool ResizeIsMaxEdge => Resize == 1;
    public bool ResizeIsPercent => Resize == 2;

    public string TargetText => TargetDirectory is { Length: > 0 } directory
        ? directory
        : "next to the original";

    public string QualityText => $"{Quality}";

    [RelayCommand]
    private void SelectFormat(string index)
    {
        if (int.TryParse(index, out var value)) Format = value;
    }

    [RelayCommand]
    private void SelectResize(string index)
    {
        if (int.TryParse(index, out var value)) Resize = value;
    }

    partial void OnFormatChanged(int value)
    {
        OnPropertyChanged(nameof(IsPng));
        OnPropertyChanged(nameof(IsJpeg));
        OnPropertyChanged(nameof(IsWebP));
        OnPropertyChanged(nameof(HasQuality));
        Save();
    }

    partial void OnResizeChanged(int value)
    {
        OnPropertyChanged(nameof(ResizeIsNone));
        OnPropertyChanged(nameof(ResizeIsMaxEdge));
        OnPropertyChanged(nameof(ResizeIsPercent));
        Save();
    }

    partial void OnQualityChanged(int value)
    {
        OnPropertyChanged(nameof(QualityText));
        Save();
    }

    partial void OnMaxEdgeChanged(int value) => Save();

    partial void OnPercentChanged(int value) => Save();

    partial void OnOverwriteChanged(bool value) => Save();

    partial void OnTargetDirectoryChanged(string? value)
    {
        OnPropertyChanged(nameof(TargetText));
        Save();
    }

    private void Save()
    {
        // The guard comes first, not in the method below: [ObservableProperty] callbacks fire while
        // the constructor is still running.
        if (_loading) return;

        _context.Settings.Save(new ImageConvertSettings
        {
            Format = Format,
            Quality = Quality,
            Resize = Resize,
            MaxEdge = MaxEdge,
            Percent = Percent,
            TargetDirectory = TargetDirectory,
            Overwrite = Overwrite,
            Video = Video,
        });
    }

    // --------------------------------------------------------------- Run

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty] public partial bool MessageIsError { get; set; }

    [ObservableProperty] public partial double Progress { get; set; }

    [ObservableProperty] public partial string QueueHint { get; set; } = "";

    public bool HasJobs => Jobs.Count > 0;

    public bool HasQueueHint => QueueHint.Length > 0;

    /// <summary>Something is dragged over the window. Colors the drop area.</summary>
    [ObservableProperty] public partial bool IsDragOver { get; set; }

    private void RefreshQueue()
    {
        var ready = Jobs.Count(j => j.State == JobState.Ready);

        // Same lesson as in the YouTube module: a list that is ready looks like a list that works.
        // The missing click must be stated.
        QueueHint = !IsBusy && ready > 0
            ? ready == 1
                ? "1 image ready. Start with \"Convert\"."
                : $"{ready} images ready. Start with \"Convert\"."
            : "";

        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(HasQueueHint));
        OnPropertyChanged(nameof(HasVideos));
    }

    [RelayCommand]
    private async Task AddFilesAsync()
    {
        var picked = await _context.Shell.PickFilesAsync("Choose images", multiple: true,
            new FilePattern("Images and videos", [.. Converter.AllExtensions.Select(e => e.TrimStart('.'))]));

        Add(picked);
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (await _context.Shell.PickFolderAsync("Folder with images") is not { } folder) return;

        try
        {
            Add([.. Directory.EnumerateFiles(folder).Where(Converter.LooksLikeMedia)]);
        }
        catch (Exception ex)
        {
            Show($"Folder unreadable: {ex.Message}", error: true);
        }
    }

    /// <summary>Adds files and folders, including ones dragged from Explorer.</summary>
    public void Add(IEnumerable<string> paths)
    {
        var known = Jobs.Select(j => j.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var skipped = 0;

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path).Where(Converter.LooksLikeMedia))
                    {
                        if (known.Add(file)) { Jobs.Add(new ImageJob(file)); added++; }
                    }

                    continue;
                }

                if (!Converter.LooksLikeMedia(path)) { skipped++; continue; }
                if (!known.Add(path)) continue;

                Jobs.Add(new ImageJob(path));
                added++;
            }
            catch (Exception)
            {
                skipped++;
            }
        }

        Show(skipped == 0
            ? $"{added} images added."
            : $"{added} images added, {skipped} skipped (no known image format).");
    }

    [RelayCommand]
    private void Clear()
    {
        Jobs.Clear();
        Message = null;
    }

    [RelayCommand]
    private void RemoveJob(ImageJob? job)
    {
        if (job is not null) Jobs.Remove(job);
    }

    [RelayCommand]
    private void ClearDone()
    {
        foreach (var done in Jobs.Where(j => j.IsDone).ToList()) Jobs.Remove(done);
    }

    [RelayCommand]
    private async Task PickTargetAsync()
    {
        if (await _context.Shell.PickFolderAsync("Target folder") is { } folder) TargetDirectory = folder;
    }

    [RelayCommand]
    private void ResetTarget() => TargetDirectory = null;

    [RelayCommand]
    private void Reveal(ImageJob? job)
    {
        if (job?.OutputPath is { Length: > 0 } path) _context.Shell.RevealInExplorer(path);
    }

    [RelayCommand]
    private void Cancel() => _run?.Cancel();

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy) return;

        var pending = Jobs.Where(j => j.State is JobState.Ready or JobState.Failed).ToList();

        if (pending.Count == 0)
        {
            Show("No pending images in the list.");
            return;
        }

        _run = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        var token = _run.Token;

        IsBusy = true;
        Progress = 0;
        RefreshQueue();

        // Without ffmpeg HEIC, AVIF and TIFF are missing. Look it up once, not per image: the search
        // covers three folders and the PATH.
        var ffmpeg = _context.Tools.Find("ffmpeg");
        var temp = Path.Combine(_context.DataDirectory, "temp");

        var settings = new ConvertSettings(
            (TargetFormat)Math.Clamp(Format, 0, 2),
            Quality,
            (ResizeMode)Math.Clamp(Resize, 0, 2),
            Math.Max(16, MaxEdge),
            Math.Clamp(Percent, 1, 100),
            TargetDirectory,
            Overwrite,
            (VideoContainer)Math.Clamp(Video, 0, 1));

        var done = 0;
        var failed = 0;
        long before = 0, after = 0;

        try
        {
            foreach (var job in pending)
            {
                token.ThrowIfCancellationRequested();

                job.State = JobState.Running;
                job.StatusText = "";

                ActivityChanged?.Invoke(this, new ModuleActivity(
                    true, $"Converting: {job.Name}", (double)done / pending.Count));

                var result = await Converter.ConvertAsync(job.Path, settings, ffmpeg, temp, token);

                job.Apply(result);

                if (result.Ok)
                {
                    before += result.BeforeBytes;
                    after += result.AfterBytes;
                }
                else
                {
                    failed++;
                }

                done++;
                Progress = (double)done / pending.Count;
            }

            var saved = before - after;

            Show(failed == 0
                ? $"{done} images converted. Total {Humanize.Bytes(before)} → {Humanize.Bytes(after)}"
                  + (saved > 0 ? $", {Humanize.Bytes(saved)} less." : ".")
                : $"{done - failed} converted, {failed} failed. Reasons in the list.",
                error: failed > 0);
        }
        catch (OperationCanceledException)
        {
            foreach (var job in pending.Where(j => j.IsRunning)) job.State = JobState.Ready;
            Show($"Cancelled. {done} of {pending.Count} done.");
        }
        catch (Exception ex)
        {
            _context.Log.Error("Converting stopped.", ex);
            Show($"Converting failed: {ex.Message}", error: true);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;

            ActivityChanged?.Invoke(this, ModuleActivity.Idle);

            _run?.Dispose();
            _run = null;

            RefreshQueue();
            Cleanup(temp);
        }
    }

    /// <summary>Removes the intermediate files of the ffmpeg detour.</summary>
    /// <remarks>
    /// The converter deletes its own intermediate; this is about what a cancel mid-run leaves behind.
    /// Otherwise the data folder would grow with every cancelled batch.
    /// </remarks>
    private static void Cleanup(string temp)
    {
        try
        {
            if (!Directory.Exists(temp)) return;

            foreach (var file in Directory.EnumerateFiles(temp))
            {
                try { File.Delete(file); } catch (Exception) { }
            }
        }
        catch (Exception)
        {
        }
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    partial void OnIsBusyChanged(bool value) => RefreshQueue();
}
