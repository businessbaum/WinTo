using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Video;

/// <summary>A choice in one of the export lists.</summary>
public sealed record VideoOption(string Name, string Value)
{
    public override string ToString() => Name;
}

/// <summary>Plays one video and turns it into another: shorter, smaller, another format.</summary>
/// <remarks>
/// <para>
/// <b>The player follows the view.</b> Its window exists only while the view is shown (see
/// <see cref="VideoSurface"/>); position, volume and range live here and are handed to the next player.
/// </para>
/// <para>
/// <b>Exports never touch the original</b>: they go next to it as "name (edited).mp4", numbered if taken.
/// </para>
/// </remarks>
public sealed partial class VideoViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private readonly DispatcherTimer _clock;
    private MediaPlayer? _player;
    private nint _window;
    private bool _fromPlayer;
    private CancellationTokenSource? _export;

    public VideoViewModel(IModuleContext context)
    {
        _context = context;

        Formats =
        [
            new("Same as the original", "same"),
            new("MP4 (H.264)", "mp4"),
            new("WebM (VP9)", "webm"),
            new("GIF", "gif"),
            new("MP3, sound only", "mp3"),
            new("M4A, sound only", "m4a"),
        ];
        Heights = [new("Original size", "0"), new("2160p", "2160"), new("1440p", "1440"), new("1080p", "1080"), new("720p", "720"), new("480p", "480")];
        Sizes = [new("No limit", "0"), new("10 MB · Discord", "10"), new("25 MB", "25"), new("50 MB", "50"), new("100 MB", "100"), new("500 MB", "500")];
        Speeds = [new("0.5×", "0.5"), new("Normal", "1"), new("1.5×", "1.5"), new("2×", "2")];

        SelectedFormat = Formats[0];
        SelectedHeight = Heights[0];
        SelectedSize = Sizes[0];
        SelectedSpeed = Speeds[1];

        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
    }

    public event EventHandler<ModuleActivity>? ActivityChanged;

    // --------------------------------------------------------------- The video

    [ObservableProperty] public partial string? Path { get; set; }
    [ObservableProperty] public partial string Title { get; set; } = "Video";
    [ObservableProperty] public partial bool HasVideo { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }
    [ObservableProperty] public partial string InfoText { get; set; } = "";
    [ObservableProperty] public partial bool ShowPanel { get; set; }

    private VideoInfo? _info;

    public bool IsEmpty => !HasVideo && Problem is null;

    partial void OnHasVideoChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PlanText));
    }
    partial void OnProblemChanged(string? value) => OnPropertyChanged(nameof(IsEmpty));

    public async Task OpenAsync(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Title = System.IO.Path.GetFileName(Path);
        Problem = null;
        _info = null;
        InfoText = "";
        Position = 0;
        Duration = 0;
        Start = 0;
        End = 0;

        if (_window != 0) OpenPlayer(0);

        if (_context.Tools.Find("ffprobe") is { } ffprobe)
        {
            try
            {
                _info = await VideoTools.ProbeAsync(ffprobe, Path, _context.Shutdown);
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"ffprobe: {ex.Message}");
            }
        }

        if (_info is { } info)
        {
            InfoText = info.Summary;
            if (info.Duration.TotalSeconds > 0) SetDuration(info.Duration.TotalSeconds);
        }
        else if (_context.Tools.Find("ffmpeg") is null)
        {
            InfoText = "ffmpeg is needed for editing. winget install Gyan.FFmpeg";
        }

        HasVideo = Problem is null;
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var files = await _context.Shell.PickFilesAsync("Open a video", multiple: false, new FilePattern("Videos", VideoTools.Extensions));
        if (files.Count > 0) await OpenAsync(files[0]);
    }

    private void SetDuration(double seconds)
    {
        if (seconds <= 0 || Math.Abs(Duration - seconds) < 0.01) return;

        var keepRange = End > 0 && End < Duration;
        Duration = seconds;
        if (!keepRange) End = seconds;
    }

    // --------------------------------------------------------------- Player

    /// <summary>The view's window exists: open the video into it where it was.</summary>
    public void AttachWindow(nint window)
    {
        _window = window;
        if (Path is not null) OpenPlayer(Position);
        _clock.Start();
    }

    /// <summary>The view's window goes: remember where the video was.</summary>
    public void DetachWindow()
    {
        _clock.Stop();
        if (_player is not null) Position = _player.Position.TotalSeconds;
        _player?.Dispose();
        _player = null;
        _window = 0;
        IsPlaying = false;
    }

    private void OpenPlayer(double at)
    {
        _player?.Dispose();
        _player = null;
        if (Path is not { } path || _window == 0) return;

        var (player, problem) = MediaPlayer.Open(_window, path);
        if (player is null)
        {
            Problem = problem;
            HasVideo = false;
            return;
        }

        _player = player;
        player.Volume = (float)(Volume / 100);
        player.Muted = Muted;
        if (at > 0) player.Position = TimeSpan.FromSeconds(at);
    }

    public void UpdateVideo() => _player?.UpdateVideo();

    private void Tick()
    {
        if (_player is not { } player) return;

        if (player.Duration is { } duration) SetDuration(duration.TotalSeconds);

        _fromPlayer = true;
        Position = player.Position.TotalSeconds;
        IsPlaying = player.IsPlaying;

        // The kept range ends playback, like a preview of the cut.
        if (IsPlaying && End > 0 && End < Duration - 0.05 && Position >= End)
        {
            player.Pause();
            IsPlaying = false;
        }

        _fromPlayer = false;
    }

    [ObservableProperty] public partial double Position { get; set; }
    [ObservableProperty] public partial double Duration { get; set; }
    [ObservableProperty] public partial bool IsPlaying { get; set; }
    [ObservableProperty] public partial double Volume { get; set; } = 80;
    [ObservableProperty] public partial bool Muted { get; set; }

    public string PositionText => $"{Clock(Position)} / {Clock(Duration)}";

    partial void OnPositionChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        if (!_fromPlayer && _player is not null) _player.Position = TimeSpan.FromSeconds(value);
    }

    partial void OnDurationChanged(double value) => OnPropertyChanged(nameof(PositionText));

    partial void OnVolumeChanged(double value)
    {
        if (_player is not null) _player.Volume = (float)(value / 100);
    }

    partial void OnMutedChanged(bool value)
    {
        if (_player is not null) _player.Muted = value;
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_player is not { } player) return;

        if (player.IsPlaying)
        {
            player.Pause();
            IsPlaying = false;
            return;
        }

        // From the start of the kept range when the playhead is outside it.
        if (Position < Start - 0.05 || (End > 0 && Position >= End - 0.05)) player.Position = TimeSpan.FromSeconds(Start);
        player.Play();
        IsPlaying = true;
    }

    public void Pause()
    {
        _player?.Pause();
        IsPlaying = false;
    }

    [RelayCommand]
    private void Skip(string seconds)
    {
        if (double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var delta))
            Position = Math.Clamp(Position + delta, 0, Duration);
    }

    [RelayCommand]
    private void ToggleMute() => Muted = !Muted;

    // --------------------------------------------------------------- Range

    [ObservableProperty] public partial double Start { get; set; }
    [ObservableProperty] public partial double End { get; set; }

    public string RangeText => Duration <= 0 ? ""
        : $"{Clock(Start)} – {Clock(End)} · {Clock(Math.Max(0, End - Start))} kept";

    partial void OnStartChanged(double value) => OnPropertyChanged(nameof(RangeText));
    partial void OnEndChanged(double value) => OnPropertyChanged(nameof(RangeText));

    [RelayCommand]
    private void MarkStart() => Start = Math.Min(Position, End - 0.1);

    [RelayCommand]
    private void MarkEnd() => End = Math.Max(Position, Start + 0.1);

    [RelayCommand]
    private void WholeVideo()
    {
        Start = 0;
        End = Duration;
    }

    // --------------------------------------------------------------- Export settings

    public ObservableCollection<VideoOption> Formats { get; }
    public ObservableCollection<VideoOption> Heights { get; }
    public ObservableCollection<VideoOption> Sizes { get; }
    public ObservableCollection<VideoOption> Speeds { get; }

    [ObservableProperty] public partial VideoOption SelectedFormat { get; set; }
    [ObservableProperty] public partial VideoOption SelectedHeight { get; set; }
    [ObservableProperty] public partial VideoOption SelectedSize { get; set; }
    [ObservableProperty] public partial VideoOption SelectedSpeed { get; set; }
    [ObservableProperty] public partial bool RemoveAudio { get; set; }
    [ObservableProperty] public partial bool ExactCut { get; set; }
    [ObservableProperty] public partial int Rotation { get; set; }

    public bool IsAudioOnly => SelectedFormat.Value is "mp3" or "m4a";

    public bool IsVideoFormat => !IsAudioOnly && SelectedFormat.Value != "gif";

    public string RotationText => Rotation == 0 ? "Not rotated" : $"Rotated {Rotation}°";

    /// <summary>What the export will do, in one line.</summary>
    public string PlanText
    {
        get
        {
            if (!HasVideo) return "";
            var plan = Plan();
            if (VideoTools.CanCopy(plan))
                return "Copies without re-encoding: fast and lossless, the cut lands on the nearest keyframe before the start.";
            if (plan.TargetMegabytes > 0) return $"Encoded twice to land at {plan.TargetMegabytes} MB.";
            return SelectedFormat.Value switch
            {
                "gif" => "15 frames per second, at most 640 pixels wide, no sound.",
                "mp3" or "m4a" => "Only the sound.",
                _ => "Encoded at high quality.",
            };
        }
    }

    partial void OnSelectedFormatChanged(VideoOption value) => NotifyPlan();
    partial void OnSelectedHeightChanged(VideoOption value) => NotifyPlan();
    partial void OnSelectedSizeChanged(VideoOption value) => NotifyPlan();
    partial void OnSelectedSpeedChanged(VideoOption value) => NotifyPlan();
    partial void OnRemoveAudioChanged(bool value) => NotifyPlan();
    partial void OnExactCutChanged(bool value) => NotifyPlan();

    partial void OnRotationChanged(int value)
    {
        OnPropertyChanged(nameof(RotationText));
        NotifyPlan();
    }

    private void NotifyPlan()
    {
        OnPropertyChanged(nameof(PlanText));
        OnPropertyChanged(nameof(IsAudioOnly));
        OnPropertyChanged(nameof(IsVideoFormat));
    }

    [RelayCommand]
    private void Rotate() => Rotation = (Rotation + 90) % 360;

    [RelayCommand]
    private void TogglePanel() => ShowPanel = !ShowPanel;

    private ExportPlan Plan() => new(
        TimeSpan.FromSeconds(Start),
        TimeSpan.FromSeconds(End > 0 ? End : Duration),
        SelectedFormat.Value,
        int.Parse(SelectedHeight.Value),
        int.Parse(SelectedSize.Value),
        RemoveAudio,
        Rotation,
        double.Parse(SelectedSpeed.Value, System.Globalization.CultureInfo.InvariantCulture),
        ExactCut);

    // --------------------------------------------------------------- Export

    [ObservableProperty] public partial bool IsExporting { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? ExportText { get; set; }
    [ObservableProperty] public partial string? LastOutput { get; set; }

    public bool HasLastOutput => LastOutput is not null;

    partial void OnLastOutputChanged(string? value) => OnPropertyChanged(nameof(HasLastOutput));

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Path is not { } source || _info is not { } info || IsExporting) return;

        if (_context.Tools.Find("ffmpeg") is not { } ffmpeg)
        {
            _context.Shell.Notify("ffmpeg is needed for exporting. winget install Gyan.FFmpeg", StatusLevel.Warn);
            return;
        }

        var plan = Plan();
        var extension = VideoTools.ExtensionFor(plan.Format, source);
        var folder = System.IO.Path.GetDirectoryName(source)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(source);
        var target = System.IO.Path.Combine(folder, $"{name} (edited){extension}");
        for (var n = 2; File.Exists(target); n++) target = System.IO.Path.Combine(folder, $"{name} (edited {n}){extension}");

        Directory.CreateDirectory(_context.DataDirectory);
        var passLog = System.IO.Path.Combine(_context.DataDirectory, "pass");
        var commands = VideoTools.Commands(plan, info, source, target, passLog);
        var output = (plan.End - plan.Start).TotalSeconds / plan.Speed;

        Pause();
        IsExporting = true;
        Progress = 0;
        ExportText = "Starting…";
        LastOutput = null;
        ActivityChanged?.Invoke(this, new ModuleActivity(true, $"Exporting {System.IO.Path.GetFileName(target)}…"));

        _export?.Dispose();
        _export = CancellationTokenSource.CreateLinkedTokenSource(_context.Shutdown);
        var cancel = _export.Token;
        var errors = new Queue<string>();

        try
        {
            for (var pass = 0; pass < commands.Count; pass++)
            {
                var index = pass;
                var code = await ExternalProcess.StreamAsync(ffmpeg, commands[pass], tieToApplication: true,
                    onOutput: line =>
                    {
                        if (VideoTools.ProgressTime(line) is not { } time || output <= 0) return;
                        var share = (index + Math.Clamp(time.TotalSeconds / output, 0, 1)) / commands.Count;
                        Dispatcher.UIThread.Post(() =>
                        {
                            Progress = share;
                            ExportText = commands.Count > 1 ? $"Pass {index + 1} of 2 · {share:P0}" : $"{share:P0}";
                        });
                    },
                    onError: line =>
                    {
                        lock (errors)
                        {
                            errors.Enqueue(line);
                            if (errors.Count > 12) errors.Dequeue();
                        }
                    },
                    workingDirectory: _context.DataDirectory,
                    cancellationToken: cancel);

                if (code == -1) throw new OperationCanceledException();
                if (code != 0)
                {
                    var why = string.Join(" ", errors.Where(e => e.Length > 0).TakeLast(2));
                    throw new InvalidOperationException(why.Length > 0 ? why : $"ffmpeg ended with code {code}.");
                }
            }

            var size = new FileInfo(target).Length;
            LastOutput = target;
            ExportText = $"Saved: {System.IO.Path.GetFileName(target)} · {Humanize.Bytes(size)}";
            _context.Shell.Notify($"Saved: {System.IO.Path.GetFileName(target)} · {Humanize.Bytes(size)}.", StatusLevel.Good);
        }
        catch (OperationCanceledException)
        {
            ExportText = "Stopped.";
            TryDelete(target);
        }
        catch (Exception ex)
        {
            _context.Log.Error("Export failed.", ex);
            ExportText = $"Not exported: {ex.Message}";
            TryDelete(target);
        }
        finally
        {
            IsExporting = false;
            Progress = 0;
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
            foreach (var log in Directory.EnumerateFiles(_context.DataDirectory, "pass*")) TryDelete(log);
        }
    }

    [RelayCommand]
    private void CancelExport() => _export?.Cancel();

    [RelayCommand]
    private void RevealOutput()
    {
        if (LastOutput is { } output) _context.Shell.RevealInExplorer(output);
    }

    /// <summary>The frame under the playhead as a PNG next to the video.</summary>
    [RelayCommand]
    private async Task SnapshotAsync()
    {
        if (Path is not { } source || _context.Tools.Find("ffmpeg") is not { } ffmpeg) return;

        var time = TimeSpan.FromSeconds(Position);
        var target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!,
            $"{System.IO.Path.GetFileNameWithoutExtension(source)} {time:hh\\-mm\\-ss}.png");

        var result = await ExternalProcess.RunAsync(ffmpeg,
            ["-y", "-hide_banner", "-v", "error", "-ss", VideoTools.Format(time), "-i", source, "-frames:v", "1", target]);

        _context.Shell.Notify(result.Ok ? $"Saved: {System.IO.Path.GetFileName(target)}." : $"No frame saved: {result.Problem}",
            result.Ok ? StatusLevel.Good : StatusLevel.Bad);
    }

    [RelayCommand]
    private void Reveal()
    {
        if (Path is { } path) _context.Shell.RevealInExplorer(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // A half-written file someone holds open stays; the next export takes another name.
        }
    }

    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss\.f");
    }

    public void Dispose()
    {
        _clock.Stop();
        _export?.Cancel();
        _export?.Dispose();
        _player?.Dispose();
        _player = null;
    }
}
