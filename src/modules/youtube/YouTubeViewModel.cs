using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.YouTube;

/// <summary>What goes into the module's <c>settings.json</c>.</summary>
public sealed class YouTubeSettings
{
    public string TargetDirectory { get; set; } = "";
    public bool AudioOnly { get; set; }
    public string AudioFormat { get; set; } = "mp3";
    public int MaxHeight { get; set; } = 1080;
    public bool WholePlaylist { get; set; }
    public bool EmbedThumbnail { get; set; } = true;
}

public enum JobState { Waiting, Downloading, Done, Failed, Cancelled }

public sealed partial class DownloadJob : ObservableObject
{
    public DownloadJob(string url, MediaInfo? info)
    {
        Url = url;
        Info = info;
        Title = info?.Title ?? url;
    }

    public string Url { get; }

    public MediaInfo? Info { get; }

    public string Title { get; }

    public string Subtitle => Info is null ? Url
        : Info.IsPlaylist ? $"Playlist · {Info.Entries.Count} items"
        : string.Join("  ·  ", new[] { Info.Uploader, Info.DurationText }.Where(s => !string.IsNullOrEmpty(s)));

    [ObservableProperty] public partial JobState State { get; set; } = JobState.Waiting;
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial bool ProgressIsKnown { get; set; }
    [ObservableProperty] public partial string? ResultPath { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "waiting for \"Download\"";

    public bool IsDone => State == JobState.Done;

    /// <summary>Whether a progress bar is shown at all.</summary>
    /// <remarks>
    /// A waiting job used to show an indeterminate (i.e. running) bar, which looks like "downloading"
    /// while nothing starts until the button is pressed. Now a bar only appears when something runs.
    /// </remarks>
    public bool ShowProgress => State is JobState.Downloading or JobState.Done;

    public string StateText => State switch
    {
        JobState.Waiting => "waiting",
        JobState.Downloading => "downloading",
        JobState.Done => "done",
        JobState.Failed => "failed",
        _ => "cancelled",
    };

    partial void OnStateChanged(JobState value)
    {
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(StateText));
    }
}

public sealed partial class YouTubeViewModel : ObservableObject
{
    private readonly IModuleContext _context;
    private CancellationTokenSource? _cancellation;

    public YouTubeViewModel(IModuleContext context)
    {
        _context = context;

        var settings = context.Settings.Load<YouTubeSettings>();

        TargetDirectory = settings.TargetDirectory.Length > 0
            ? settings.TargetDirectory
            : DefaultFolder();

        AudioOnly = settings.AudioOnly;
        AudioFormat = settings.AudioFormat;
        MaxHeight = settings.MaxHeight;
        WholePlaylist = settings.WholePlaylist;
        EmbedThumbnail = settings.EmbedThumbnail;

        Jobs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasJobs));
            RaiseQueueHint();
        };

        // An explicit bool: a binding on Log.Count would silently do nothing.
        Log.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLog));
    }

    /// <summary><c>Videos\WinTo</c>; <c>Videos\MultiTool</c> where it exists, the default until 0.13.0.</summary>
    private static string DefaultFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var former = Path.Combine(videos, "MultiTool");
        return Directory.Exists(former) ? former : Path.Combine(videos, "WinTo");
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<DownloadJob> Jobs { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public bool HasJobs => Jobs.Count > 0;

    public bool HasLog => Log.Count > 0;

    /// <summary>Shown above the queue: what to do next.</summary>
    /// <remarks>A list with one entry looks busy. It isn't until someone presses "Download", which said nowhere.</remarks>
    public string QueueHint
    {
        get
        {
            if (IsBusy) return "";

            var waiting = Jobs.Count(j => j.State is JobState.Waiting or JobState.Failed);

            return waiting switch
            {
                0 => "",
                1 => "1 title waiting for \"Download\".",
                _ => $"{waiting} titles waiting for \"Download\".",
            };
        }
    }

    public bool HasQueueHint => QueueHint.Length > 0;

    private void RaiseQueueHint()
    {
        OnPropertyChanged(nameof(QueueHint));
        OnPropertyChanged(nameof(HasQueueHint));
    }

    public IReadOnlyList<string> AudioFormats { get; } = ["mp3", "m4a", "opus", "flac", "wav"];

    public IReadOnlyList<string> Qualities { get; } = ["best", "2160p", "1440p", "1080p", "720p", "480p"];

    // --------------------------------------------------------------- Input

    [ObservableProperty] public partial string Url { get; set; } = "";
    [ObservableProperty] public partial string TargetDirectory { get; set; } = "";
    [ObservableProperty] public partial bool AudioOnly { get; set; }
    [ObservableProperty] public partial string AudioFormat { get; set; } = "mp3";
    [ObservableProperty] public partial int MaxHeight { get; set; } = 1080;
    [ObservableProperty] public partial bool WholePlaylist { get; set; }
    [ObservableProperty] public partial bool EmbedThumbnail { get; set; } = true;

    /// <summary>The quality entry as text; the ComboBox binds to this.</summary>
    public string Quality
    {
        get => MaxHeight <= 0 ? "best" : $"{MaxHeight}p";
        set
        {
            MaxHeight = value == "best" ? 0
                : int.TryParse(value.TrimEnd('p'), out var height) ? height
                : 1080;

            OnPropertyChanged();
        }
    }

    [ObservableProperty] public partial bool IsBusy { get; set; }

    partial void OnIsBusyChanged(bool value) => RaiseQueueHint();
    [ObservableProperty] public partial bool IsInspecting { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    // --------------------------------------------------------------- Tools

    [ObservableProperty] public partial string ToolText { get; set; } = "checking…";
    [ObservableProperty] public partial bool ToolsReady { get; set; }
    [ObservableProperty] public partial bool IsFetchingTool { get; set; }

    private string? _ytDlp;
    private string? _ffmpegDirectory;

    /// <summary>Checks whether yt-dlp and ffmpeg are present.</summary>
    /// <remarks>
    /// Runs after the first frame: <c>yt-dlp --version</c> starts a process and can take half a second
    /// the first time, exactly the delay between tile click and window this project avoids.
    /// </remarks>
    public async Task CheckToolsAsync()
    {
        _ytDlp = YtDlp.Find(_context);

        var ffmpeg = _context.Tools.Find("ffmpeg");
        _ffmpegDirectory = ffmpeg is null ? null : Path.GetDirectoryName(ffmpeg);

        if (_ytDlp is null)
        {
            // The shell asks before opening and fetches on request; landing here means someone chose
            // "Open without" or the download failed.
            ToolsReady = false;
            ToolText = ffmpeg is null
                ? "yt-dlp and ffmpeg are missing. The shell asks about them when the tab is opened again."
                : "yt-dlp is missing. The shell asks about it when the tab is opened again.";
            return;
        }

        var version = await YtDlp.VersionAsync(_ytDlp, _context.Shutdown);

        ToolsReady = ffmpeg is not null;

        ToolText = ffmpeg is null
            ? $"yt-dlp {version} · ffmpeg missing. Video and audio can't be merged."
            : $"yt-dlp {version} · ffmpeg found";
    }

    [RelayCommand]
    private async Task UpdateToolAsync()
    {
        if (_ytDlp is null) return;

        IsFetchingTool = true;
        ToolText = "yt-dlp is updating…";

        var result = await YtDlp.UpdateAsync(_ytDlp, _context.Shutdown);

        IsFetchingTool = false;
        Show(result.Ok ? result.Output.Trim() : result.Problem, error: !result.Ok);

        await CheckToolsAsync();
    }

    // --------------------------------------------------------------- Jobs

    [RelayCommand]
    private async Task AddAsync()
    {
        var url = Url.Trim();
        if (url.Length == 0) return;

        if (_ytDlp is null)
        {
            Show("yt-dlp is missing. The shell asks about it when the tab is opened again.", error: true);
            return;
        }

        IsInspecting = true;

        try
        {
            // The URL is inspected before it enters the list; otherwise a raw link would sit there and
            // a typo would only show on start.
            var info = await YtDlp.InspectAsync(_ytDlp, url, _ffmpegDirectory, _context.Shutdown);

            if (info is null)
            {
                Show("yt-dlp can't resolve this URL. Check the input.", error: true);
                return;
            }

            Jobs.Add(new DownloadJob(url, info));
            Url = "";
            Message = null;
        }
        finally
        {
            IsInspecting = false;
        }
    }

    private void PublishTargetStatus()
    {
        try
        {
            var count = Directory.Exists(TargetDirectory)
                ? Directory.EnumerateFiles(TargetDirectory).Count()
                : 0;

            _context.PublishStatus(new ModuleStatus(
                count > 0 ? StatusLevel.Good : StatusLevel.Neutral,
                count == 1 ? "1 file in the target folder" : $"{count} files in the target folder",
                DateTimeOffset.Now, TargetDirectory));
        }
        catch (Exception)
        {
            // Without a count the last state stays.
        }
    }

    [RelayCommand]
    private void RemoveJob(DownloadJob? job)
    {
        if (job is not null && job.State != JobState.Downloading) Jobs.Remove(job);
    }

    [RelayCommand]
    private void ClearDone()
    {
        foreach (var job in Jobs.Where(j => j.State is JobState.Done or JobState.Cancelled).ToList())
            Jobs.Remove(job);
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (await _context.Shell.PickFolderAsync("Target folder") is { } folder)
        {
            TargetDirectory = folder;
            Save();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(TargetDirectory);
        _context.Shell.RevealInExplorer(TargetDirectory);
    }

    [RelayCommand]
    private void Reveal(DownloadJob? job)
    {
        if (job?.ResultPath is { } path && File.Exists(path)) _context.Shell.RevealInExplorer(path);
        else OpenFolder();
    }

    // --------------------------------------------------------------- download

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsBusy || _ytDlp is null) return;

        var todo = Jobs.Where(j => j.State is JobState.Waiting or JobState.Failed).ToList();
        if (todo.Count == 0) return;

        Directory.CreateDirectory(TargetDirectory);

        IsBusy = true;
        Message = null;
        Log.Clear();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);

        var done = 0;

        try
        {
            // One after another: parallel downloads share the line and YouTube throttles multiple
            // connections from one source. The batch wouldn't finish sooner, but the first title later.
            foreach (var job in todo)
            {
                _cancellation.Token.ThrowIfCancellationRequested();

                // todo is a snapshot. Removing a waiting job during the batch is meant; otherwise the title
                // would still land in the target folder from a visibly empty queue.
                if (!Jobs.Contains(job)) continue;

                await RunAsync(job, _cancellation.Token);

                if (job.State == JobState.Done) done++;
            }

            var failed = todo.Count(j => j.State == JobState.Failed);

            Show(failed == 0 ? $"{done} done." : $"{done} done, {failed} failed.", error: failed > 0);

            // A state, not a report: "3 downloaded" would still be on the tile days later. Count what is
            // in the target folder now, like the recorder.
            PublishTargetStatus();
        }
        catch (OperationCanceledException)
        {
            foreach (var job in todo.Where(j => j.State == JobState.Downloading))
            {
                job.State = JobState.Cancelled;
                job.StatusText = "cancelled";
            }

            Show($"Cancelled after {done} titles.");
        }
        finally
        {
            IsBusy = false;
            _cancellation?.Dispose();
            _cancellation = null;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }
    }

    private async Task RunAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        job.State = JobState.Downloading;
        job.StatusText = "starting…";
        job.Progress = 0;
        job.ProgressIsKnown = false;

        var request = new YtDlp.DownloadRequest(
            Url: job.Url,
            TargetDirectory: TargetDirectory,
            AudioOnly: AudioOnly,
            AudioFormat: AudioFormat,
            MaxHeight: MaxHeight,
            WholePlaylist: WholePlaylist && job.Info?.IsPlaylist == true,
            EmbedThumbnail: EmbedThumbnail,
            FfmpegDirectory: _ffmpegDirectory);

        var errors = new List<string>();

        var code = await ExternalProcess.StreamAsync(
            _ytDlp!,
            YtDlp.BuildArguments(request),

            // Both callbacks come from a background thread; anything touching a bound property goes
            // through the dispatcher.
            onOutput: line => Dispatcher.UIThread.Post(() => OnLine(job, line)),
            onError: line =>
            {
                lock (errors) errors.Add(line);
                Dispatcher.UIThread.Post(() => Append(line));
            },
            cancellationToken: cancellationToken);

        RaiseQueueHint();

        if (code == 0)
        {
            job.State = JobState.Done;
            job.Progress = 1;
            job.ProgressIsKnown = true;
            job.StatusText = job.ResultPath is { } path ? Path.GetFileName(path) : "done";
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            job.State = JobState.Cancelled;
            job.StatusText = "cancelled";
            return;
        }

        job.State = JobState.Failed;

        lock (errors)
        {
            // The last error line is almost always the telling one: retries before it, nothing after.
            job.StatusText = errors.Count > 0 ? errors[^1] : $"yt-dlp exited with code {code}";
        }
    }

    private void OnLine(DownloadJob job, string line)
    {
        if (YtDlp.ReadDestination(line) is { } destination) job.ResultPath = destination;

        if (YtDlp.ReadProgress(line) is { } progress)
        {
            job.ProgressIsKnown = progress.Fraction is not null;
            job.Progress = progress.Fraction ?? 0;
            job.StatusText = progress.Detail.Length > 0
                ? $"{progress.Phase} · {progress.Detail}"
                : progress.Phase;

            ActivityChanged?.Invoke(this, new ModuleActivity(
                true, $"{job.Title} — {job.StatusText}", progress.Fraction));

            // No progress lines in the log: a long video produces thousands, burying the interesting one.
            return;
        }

        Append(line);
    }

    private void Append(string line)
    {
        Log.Add(line);

        // A log that keeps growing is a memory problem after a playlist; the last 200 lines are
        // all anyone reads.
        while (Log.Count > 200) Log.RemoveAt(0);
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    // --------------------------------------------------------------- Helpers

    partial void OnAudioOnlyChanged(bool value) => Save();
    partial void OnAudioFormatChanged(string value) => Save();
    partial void OnMaxHeightChanged(int value) => Save();
    partial void OnWholePlaylistChanged(bool value) => Save();
    partial void OnEmbedThumbnailChanged(bool value) => Save();

    private void Save() => _context.Settings.Save(new YouTubeSettings
    {
        TargetDirectory = TargetDirectory,
        AudioOnly = AudioOnly,
        AudioFormat = AudioFormat,
        MaxHeight = MaxHeight,
        WholePlaylist = WholePlaylist,
        EmbedThumbnail = EmbedThumbnail,
    });

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }
}
