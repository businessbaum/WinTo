using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Capture.Recording;

/// <summary>What ends up in the module's <c>settings.json</c>.</summary>
public sealed class RecorderSettings
{
    public string TargetDirectory { get; set; } = "";
    public int FramesPerSecond { get; set; } = 30;
    public int Quality { get; set; } = 23;
    public bool CaptureCursor { get; set; } = true;
    public int BufferMinutes { get; set; } = 5;
    public string AreaName { get; set; } = "";
    public string Encoder { get; set; } = "";

    /// <summary>The selected recording devices.</summary>
    /// <remarks>
    /// <b><c>null</c> means "never chosen", an empty list means "deliberately none".</b> On first use everything that
    /// looks like a loopback is preselected; without the distinction an empty list would look like "nothing chosen
    /// yet" on the next start and the preselection would tick it again.
    /// </remarks>
    public List<string>? AudioDevices { get; set; }

    /// <summary>Include system audio, what comes out of the speakers.</summary>
    /// <remarks>
    /// Defaults to <c>true</c>, also for an existing <c>settings.json</c> missing the field: a missing field keeps
    /// the initial value. Intended: anyone who got silent clips so far shouldn't have to find a setting first.
    /// </remarks>
    public bool SystemAudio { get; set; } = true;

    /// <summary>Empty means the current default playback device.</summary>
    public string SystemAudioDeviceId { get; set; } = "";

    /// <summary>The key combination label, e.g. "Ctrl+Alt+C".</summary>
    public string ClipHotkey { get; set; } = "";

    /// <summary>Bring microphone and system audio to the same loudness.</summary>
    /// <remarks>
    /// On by default, also for an existing <c>settings.json</c> without the field. Without leveling, mic gain and
    /// Windows volume decide who is audible in the clip (measured 28.6 dB apart).
    /// </remarks>
    public bool LevelMatch { get; set; } = true;

    /// <summary>Gain adjustment for recording devices in dB.</summary>
    public double DeviceGainDb { get; set; }

    /// <summary>Gain adjustment for system audio in dB.</summary>
    public double SystemGainDb { get; set; }
}

/// <summary>An audio device with a checkbox.</summary>
public sealed partial class AudioChoice(AudioDevice device, bool selected) : ObservableObject
{
    public AudioDevice Device { get; } = device;

    public string Name => Device.Name;

    public bool LooksLikeLoopback => Device.LooksLikeLoopback;

    [ObservableProperty] public partial bool Selected { get; set; } = selected;
}

public sealed partial class RecorderViewModel : ObservableObject, IDisposable
{
    /// <summary>Length of one buffer segment.</summary>
    /// <remarks>
    /// Sets the ring's granularity, not the clip's precision (that comes from <see cref="KeyframeSeconds"/> and the
    /// final trim). A segment is what the ring discards at once, so the buffer always reaches at least as far back
    /// as set and at most 15 s further. Shorter segments would mean more files and headers for nothing.
    /// </remarks>
    private const int SegmentSeconds = 15;

    /// <summary>Keyframe interval. Divides <see cref="SegmentSeconds"/>, otherwise none falls on the segment boundary.</summary>
    /// <remarks>
    /// Also sets how precisely a clip ends: with <c>-c copy</c> cuts only happen at keyframes, so a clip can be up to
    /// three seconds longer than set. Never shorter.
    /// </remarks>
    private const int KeyframeSeconds = 3;

    private readonly IModuleContext _context;
    private readonly Recorder _recorder = new();
    private readonly DispatcherTimer _timer;
    private readonly GlobalHotkey _hotkey = new();

    private string? _ffmpeg;
    private string? _currentFile;
    private bool _bufferMode;

    /// <summary>While set, <see cref="Save"/> writes nothing.</summary>
    /// <remarks>
    /// <b>Without this guard opening the module overwrote its own settings.</b> The constructor sets
    /// <c>SelectedArea</c>; the <c>[ObservableProperty]</c> callback fires immediately and calls <c>Save</c> while
    /// <c>SelectedEncoder</c> is still <c>null</c> and <c>AudioChoices</c> empty. Closing the module in that window
    /// lost the settings. The guard belongs at the start, not in the method behind it.
    /// </remarks>
    private bool _loading = true;

    public RecorderViewModel(IModuleContext context)
    {
        _context = context;

        var settings = context.Settings.Load<RecorderSettings>();

        TargetDirectory = settings.TargetDirectory.Length > 0
            ? settings.TargetDirectory
            : DefaultFolder();

        FramesPerSecond = settings.FramesPerSecond;
        Quality = settings.Quality;
        CaptureCursor = settings.CaptureCursor;
        BufferMinutes = Math.Clamp(settings.BufferMinutes, 1, 30);
        SystemAudioEnabled = settings.SystemAudio;
        _wantedArea = settings.AreaName;
        _wantedDevices = settings.AudioDevices;
        _wantedEncoder = settings.Encoder;
        _wantedRenderDevice = settings.SystemAudioDeviceId;
        SelectedHotkey = GlobalHotkey.ById(settings.ClipHotkey);
        LevelMatch = settings.LevelMatch;
        DeviceGainDb = Math.Clamp(settings.DeviceGainDb, -12, 12);
        SystemGainDb = Math.Clamp(settings.SystemGainDb, -12, 12);

        // The list stays empty until the view reports the screens. "All screens" is deliberately
        // no longer in it; see SetScreens.

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => NotifyClock();

        _recorder.Line += OnRecorderLine;
        _recorder.Exited += OnRecorderExited;
        _hotkey.Pressed += OnHotkeyPressed;

        // Avalonia doesn't convert int to bool in a binding; "{Binding Log.Count}" would silently do nothing.
        Log.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasLog));
            OnPropertyChanged(nameof(LogToggleText));
        };

        _loading = false;
    }

    /// <summary><c>Videos\WinTo</c>; <c>Videos\MultiTool</c> where it exists, the default until 0.13.0.</summary>
    private static string DefaultFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var former = Path.Combine(videos, "MultiTool");
        return Directory.Exists(former) ? former : Path.Combine(videos, "WinTo");
    }

    private readonly string _wantedArea;
    private readonly string _wantedEncoder;
    private readonly List<string>? _wantedDevices;
    private readonly string _wantedRenderDevice;

    /// <summary>Runs while system audio flows into a recording.</summary>
    private SystemAudio? _systemAudio;

    public event EventHandler<ModuleActivity>? ActivityChanged;

    public ObservableCollection<CaptureArea> Areas { get; } = [];

    public ObservableCollection<AudioChoice> AudioChoices { get; } = [];

    /// <summary>The playback devices, to choose whose sound is captured.</summary>
    public ObservableCollection<RenderDevice> RenderDevices { get; } = [];

    public ObservableCollection<string> Recent { get; } = [];

    public ObservableCollection<string> Log { get; } = [];

    public bool HasLog => Log.Count > 0;

    /// <summary>The ffmpeg output below the recent files; closed unless something went wrong.</summary>
    [ObservableProperty]
    public partial bool ShowLog { get; set; }

    public string LogToggleText => $"ffmpeg output ({Log.Count} {(Log.Count == 1 ? "line" : "lines")})";

    /// <summary>Only those proven to start on this computer.</summary>
    public ObservableCollection<EncoderChoice> Encoders { get; } = [];

    // --------------------------------------------------------------- Settings

    [ObservableProperty] public partial CaptureArea? SelectedArea { get; set; }
    [ObservableProperty] public partial string TargetDirectory { get; set; } = "";
    [ObservableProperty] public partial int FramesPerSecond { get; set; } = 30;
    [ObservableProperty] public partial int Quality { get; set; } = 23;
    [ObservableProperty] public partial bool CaptureCursor { get; set; } = true;
    [ObservableProperty] public partial int BufferMinutes { get; set; } = 5;
    [ObservableProperty] public partial EncoderChoice? SelectedEncoder { get; set; }

    /// <summary>Include system audio. On by default; see <see cref="RecorderSettings.SystemAudio"/>.</summary>
    [ObservableProperty] public partial bool SystemAudioEnabled { get; set; } = true;

    [ObservableProperty] public partial RenderDevice? SelectedRenderDevice { get; set; }

    /// <summary>Brings every audio source to the same loudness before mixing.</summary>
    /// <remarks>
    /// Measured on two sources 28.6 dB apart: <b>0.0 dB</b> afterwards. The mix lands at −18 LUFS with −6.8 dB peak,
    /// so it doesn't clip.
    /// </remarks>
    [ObservableProperty] public partial bool LevelMatch { get; set; } = true;

    /// <summary>Microphone louder or quieter than system audio, in dB.</summary>
    [ObservableProperty] public partial double DeviceGainDb { get; set; }

    [ObservableProperty] public partial double SystemGainDb { get; set; }

    public string DeviceGainText => GainText(DeviceGainDb);

    public string SystemGainText => GainText(SystemGainDb);

    /// <summary>Signed: "+3 dB" says more than "3 dB" when it can also go down.</summary>
    private static string GainText(double db) =>
        Math.Abs(db) < 0.05 ? "0 dB" : $"{db:+0.#;-0.#} dB";

    /// <summary>The available key combinations.</summary>
    public IReadOnlyList<HotkeyChoice> Hotkeys => GlobalHotkey.Choices;

    [ObservableProperty] public partial HotkeyChoice SelectedHotkey { get; set; } = GlobalHotkey.Default;

    /// <summary>The line below the picker.</summary>
    /// <remarks>
    /// Windows hands out a combination only once. A key you believe clips but stays silent is worse than none.
    /// </remarks>
    [ObservableProperty] public partial string HotkeyText { get; set; } = "";

    /// <summary>Name of the current Windows default device, for the line below the list.</summary>
    [ObservableProperty] public partial string DefaultRenderName { get; set; } = "";

    /// <summary>Only visible while following the default output.</summary>
    public bool FollowsDefaultRender => SelectedRenderDevice?.IsFollows ?? true;

    partial void OnSystemAudioEnabledChanged(bool value) => Save();

    partial void OnLevelMatchChanged(bool value) => Save();

    partial void OnDeviceGainDbChanged(double value)
    {
        OnPropertyChanged(nameof(DeviceGainText));
        Save();
    }

    partial void OnSystemGainDbChanged(double value)
    {
        OnPropertyChanged(nameof(SystemGainText));
        Save();
    }

    partial void OnSelectedHotkeyChanged(HotkeyChoice value)
    {
        Save();

        // Only re-register if it should apply now; otherwise the module occupies a combination
        // with nothing waiting for it.
        if (IsBuffering) EnableHotkey();
    }

    partial void OnSelectedRenderDeviceChanged(RenderDevice? value)
    {
        OnPropertyChanged(nameof(FollowsDefaultRender));
        Save();
    }

    public IReadOnlyList<int> FrameRates { get; } = [24, 30, 48, 60];

    // --------------------------------------------------------------- State

    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial bool IsBuffering { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial string EncoderText { get; set; } = "checking…";
    [ObservableProperty] public partial string SizeText { get; set; } = "";
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial bool MessageIsError { get; set; }

    /// <summary>A note below the message that is no failure, e.g. a recording without sound. Neutral, not red.</summary>
    [ObservableProperty] public partial string? Hint { get; set; }
    [ObservableProperty] public partial bool ToolsReady { get; set; }

    /// <summary>The clock on display, started when ffmpeg is running.</summary>
    /// <remarks>
    /// Until 0.13.0 the display showed ffmpeg's <c>time=</c>. At the start that stays at 0:00 for a few seconds
    /// (encoder lookahead, the audio pipe connecting) and then jumps, which read as a broken timer. ffmpeg's
    /// stats now only give the file size.
    /// </remarks>
    private readonly Stopwatch _clock = new();

    private TimeSpan Elapsed => _clock.Elapsed;

    private string _shownClock = "";

    public string ElapsedText => Humanize.Clock(Elapsed);

    private void NotifyClock()
    {
        var text = ElapsedText;
        if (text == _shownClock) return;

        _shownClock = text;
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(DetailText));
    }

    public bool IsIdle => !IsRecording && !IsBuffering;

    /// <summary>The line below the state: file size while recording, saveable length while buffering.</summary>
    /// <remarks>
    /// <para>
    /// <b>A running clock belongs to recording, not buffering.</b> While recording the file really grows. The ring
    /// stays the same size; a clock there reads as "all of this is saved", but nothing is saved until a clip is taken.
    /// </para>
    /// <para>
    /// Instead this shows how much is in the ring. The number grows to the set length and stays there.
    /// </para>
    /// </remarks>
    public string DetailText => IsBuffering
        ? $"{Humanize.Clock(Elapsed < BufferSpan ? Elapsed : BufferSpan)} in buffer"
        : SizeText;

    private TimeSpan BufferSpan => TimeSpan.FromMinutes(BufferMinutes);

    /// <summary>Resets clock and size to zero, and notifies.</summary>
    /// <remarks>
    /// <para>
    /// <b>The notification is the point.</b> <see cref="ElapsedText"/> is computed from <see cref="_clock"/>;
    /// resetting the clock notifies nobody. A new run showed the previous run's time until the timer ticked.
    /// Same for <see cref="SizeText"/>.
    /// </para>
    /// <para>
    /// Called <b>before</b> setting <c>IsRecording</c>: the display is bound to <c>!IsIdle</c> and appears then. It
    /// should be right when it appears.
    /// </para>
    /// </remarks>
    private void ResetCounters()
    {
        _clock.Reset();
        _shownClock = "";
        NotifyClock();

        SizeText = "";
        _overloadReported = false;
    }

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));

    partial void OnIsBufferingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(CanSaveClip));
    }

    // --------------------------------------------------------------- Preparation

    /// <summary>Set by the view once it is attached to a window.</summary>
    /// <remarks>
    /// <para>
    /// Screen geometry belongs to the <c>TopLevel</c>, which only the view knows.
    /// </para>
    /// <para>
    /// <b>"All screens" no longer exists.</b> Two screens side by side can be wider than H.264 supports on the GPU,
    /// and the CPU fallback can't handle that size either: a recording that runs and produces nothing. With a
    /// single screen the option was the same as that screen anyway.
    /// </para>
    /// </remarks>
    public void SetScreens(IReadOnlyList<(string Name, int X, int Y, int Width, int Height)> screens)
    {
        // Remember the current choice before rebuilding the list.
        //
        // This method comes from OnAttachedToVisualTree, which fires on EVERY tab switch.
        // Before, only the area remembered at open was used, so choosing another screen and
        // switching tabs once reset the choice.
        var current = SelectedArea?.Name;

        Areas.Clear();

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        for (var i = 0; i < screens.Count; i++)
        {
            var (name, x, y, width, height) = screens[i];

            Areas.Add(new CaptureArea($"Screen {i + 1} · {width}×{height}{name}", x, y, width, height));

            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + width);
            bottom = Math.Max(bottom, y + height);
        }

        _desktop = Areas.Count > 0 ? (right - left, bottom - top) : (0, 0);

        if (Areas.Count == 0) return;

        SelectedArea =
            Areas.FirstOrDefault(a => a.Name == current)
            ?? Areas.FirstOrDefault(a => a.Name == _wantedArea)

            // The primary screen when the remembered choice no longer fits, e.g. it used to be
            // "All screens".
            ?? Areas.FirstOrDefault(a => a.Name.Contains("(primary)", StringComparison.Ordinal))
            ?? Areas[0];
    }

    /// <summary>Finds ffmpeg, queries encoders and audio devices.</summary>
    public async Task PrepareAsync(bool refreshEncoders = false)
    {
        _ffmpeg = _context.Tools.Find("ffmpeg");

        if (_ffmpeg is null)
        {
            ToolsReady = false;
            EncoderText = "ffmpeg not found.";
            return;
        }

        try
        {
            // Several process starts, about a second in total. After the first frame, not before,
            // and each encoder is actually tried once.
            EncoderText = "Checking encoders…";

            var encoders = await Capture.WorkingEncodersAsync(_ffmpeg, _context.Shutdown, refreshEncoders);
            var devices = await Capture.ListAudioDevicesAsync(_ffmpeg, _context.Shutdown);

            // Via Task.Run: Core Audio is COM, and enumerating endpoints costs a dozen
            // cross-apartment calls.
            var render = await Task.Run(SystemAudio.Devices, _context.Shutdown);

            RenderDevices.Clear();

            // At the top and without a device behind it: see RenderDevice.Follows.
            RenderDevices.Add(RenderDevice.Follows);
            foreach (var device in render) RenderDevices.Add(device);

            // What the default output currently points to, shown below the list so "follows
            // Windows" doesn't leave you guessing.
            DefaultRenderName = render.FirstOrDefault(d => d.IsDefault)?.Name ?? "no playback device";

            var currentDevice = SelectedRenderDevice?.Id;

            // The fallback ends at Follows, not the first device: nothing is pinned without an
            // explicit choice.
            SelectedRenderDevice =
                RenderDevices.FirstOrDefault(d => d.Id == currentDevice)
                ?? RenderDevices.FirstOrDefault(d => d.Id == _wantedRenderDevice && d.Id.Length > 0)
                ?? RenderDevice.Follows;

            Encoders.Clear();
            foreach (var encoder in encoders) Encoders.Add(Capture.ChoiceFor(encoder));

            // Current choice first, then the remembered one, then the first.
            //
            // PrepareAsync runs on EVERY view build and on "Rescan". Before, only the encoder
            // remembered at module start was used: closing and reopening the tab reset the choice
            // and silently undid an automatic fallback to libx264.
            SelectedEncoder =
                Encoders.FirstOrDefault(e => e.Id == SelectedEncoder?.Id)
                ?? Encoders.FirstOrDefault(e => e.Id == _wantedEncoder)
                ?? Encoders[0];
            ToolsReady = true;

            // Remember what is ticked before rebuilding the list.
            var previous = AudioChoices.ToDictionary(c => c.Name, c => c.Selected, StringComparer.Ordinal);

            AudioChoices.Clear();
            foreach (var device in devices)
            {
                // Three levels. What the user ticked this session wins, otherwise the selection jumped
                // back on every tab switch (PrepareAsync runs on each view build and on "Rescan").
                //
                // Then the remembered list. null there means never chosen: preselect what looks like a
                // loopback. An empty list is a decision and stays empty.
                var selected =
                    previous.TryGetValue(device.Name, out var current) ? current
                    : _wantedDevices is { } wanted ? wanted.Contains(device.Name)
                    : device.LooksLikeLoopback;

                var choice = new AudioChoice(device, selected);
                choice.PropertyChanged += (_, _) => Save();
                AudioChoices.Add(choice);
            }

            // The hint only concerns microphones now: system audio runs via WASAPI and needs no
            // DirectShow device.
            if (devices.Count == 0 && !SystemAudioEnabled)
                Show("No recording devices found and system audio off. "
                    + "The recording will have no audio.", error: true);

            RefreshRecent();
            RefreshBufferState();
        }
        catch (Exception ex)
        {
            _context.Log.Error("Device query failed.", ex);
            EncoderText = ex.Message;
        }
    }

    /// <summary>One sentence on when this encoder is the right choice.</summary>
    [ObservableProperty] public partial string EncoderHint { get; set; } = "";

    partial void OnSelectedEncoderChanged(EncoderChoice? value)
    {
        // The hint is now in each picker row. This is the line below for the SELECTED one,
        // visible even when the list is collapsed.
        EncoderText = value?.Label ?? "";
        EncoderHint = value?.Hint ?? "";
        Save();
    }

    /// <summary>Assembles the capture settings and creates the system audio source.</summary>
    /// <remarks>
    /// <b>The order is mandatory.</b> The named pipe must exist before ffmpeg starts; ffmpeg is the client and opens
    /// it like a file. So <see cref="SystemAudio"/> is created here, before building the command line containing its
    /// path, long before the process starts.
    /// </remarks>
    private CaptureSettings BuildSettings()
    {
        ReleaseSystemAudio();

        SystemAudioInput? system = null;

        if (SystemAudioEnabled)
        {
            try
            {
                var audio = new SystemAudio(SelectedRenderDevice?.Id);
                _systemAudio = audio;

                system = new SystemAudioInput(
                    audio.PipePath, audio.SampleFormat, audio.SampleRate, audio.Channels, audio.DeviceName);
            }
            catch (Exception ex)
            {
                // Without system audio a recording is still a recording. The reason goes to the log,
                // not into an abort.
                _context.Log.Warn($"System audio could not be opened: {ex.Message}");
                Log.Add($"System audio unavailable: {ex.Message}");
                ReleaseSystemAudio();
            }
        }

        return new CaptureSettings
        {
            Area = SelectedArea ?? CaptureArea.WholeDesktop,
            FramesPerSecond = FramesPerSecond,
            Quality = Quality,
            Encoder = SelectedEncoder?.Id ?? "libx264",
            CaptureCursor = CaptureCursor,
            AudioDevices = [.. AudioChoices.Where(c => c.Selected).Select(c => c.Name)],
            SystemAudio = system,
            Levels = LevelMatch ? LevelMode.Match : LevelMode.Off,
            DeviceGainDb = DeviceGainDb,
            SystemGainDb = SystemGainDb,
        };
    }

    /// <summary>Waits until ffmpeg has opened the audio pipe, then lets audio flow.</summary>
    /// <remarks>
    /// With a deadline: if another input fails (a busy microphone, a refusing encoder) ffmpeg never reaches the
    /// pipe, and without a deadline the UI would hang forever. Five seconds is plenty; ffmpeg opens inputs in order
    /// within milliseconds.
    /// </remarks>
    private async Task AttachSystemAudioAsync()
    {
        if (_systemAudio is not { } audio) return;

        if (await audio.ConnectAsync(TimeSpan.FromSeconds(5), _context.Shutdown)) return;

        Log.Add("ffmpeg did not open the audio pipe. Recording without system audio.");
        ReleaseSystemAudio();
    }

    /// <summary>Size of the whole desktop, for the encoder preflight.</summary>
    private (int Width, int Height) _desktop;

    /// <summary>What is recorded, in pixels.</summary>
    private (int Width, int Height) CaptureSize =>
        SelectedArea is { IsWholeDesktop: false } area ? (area.Width, area.Height) : _desktop;

    /// <summary>Checks before starting whether the encoder can really handle this size.</summary>
    /// <remarks>
    /// <para>
    /// <b>Measured case.</b> h264_nvenc passes the check at module open (640×480) and rejects the same recording
    /// across two screens side by side, wider than H.264 supports on that card. The fallback switched to libx264,
    /// which can't come close to that size on the CPU.
    /// </para>
    /// <para>
    /// The result was a recording that runs and produces nothing: no finished frame, counter at 0:00, only
    /// "real-time buffer too full" in the log. A single frame up front costs milliseconds.
    /// </para>
    /// </remarks>
    /// <returns><c>false</c> if nothing works; then nothing starts.</returns>
    private async Task<bool> PreflightAsync()
    {
        if (_ffmpeg is null) return false;

        // Without a selected area gdigrab would record the whole virtual desktop, exactly the size
        // that shouldn't exist here anymore. Better not to start.
        if (SelectedArea is null)
        {
            Show("No screen selected.", error: true);
            return false;
        }

        var (width, height) = CaptureSize;
        if (width <= 0 || height <= 0)
        {
            Show("Screen size unknown.", error: true);
            return false;
        }

        var encoder = SelectedEncoder?.Id ?? "libx264";

        if (await Capture.CanEncodeAsync(_ffmpeg, encoder, width, height, _context.Shutdown)) return true;

        if (encoder != "libx264"
            && await Capture.CanEncodeAsync(_ffmpeg, "libx264", width, height, _context.Shutdown))
        {
            SelectedEncoder = SoftwareEncoder() ?? SelectedEncoder;

            Show($"{Capture.EncoderLabel(encoder)} can't handle {width}×{height}, recording on the CPU. "
                 + "At this size a single screen is faster.", error: true);

            return true;
        }

        Show($"No available encoder can record {width}×{height}. "
             + "Choose a smaller area.", error: true);

        return false;
    }

    /// <summary>The software encoder from the list, the fallback when hardware refuses.</summary>
    /// <remarks>From the list rather than a new object, which keeps selection and display reliably in sync.</remarks>
    private EncoderChoice? SoftwareEncoder() =>
        Encoders.FirstOrDefault(e => e.Id == "libx264");

    private void ReleaseSystemAudio()
    {
        var audio = _systemAudio;
        _systemAudio = null;

        if (audio is null) return;

        if (audio.Dropped > 0)
            _context.Log.Warn($"System audio: {audio.Dropped} buffers dropped because ffmpeg couldn't keep up.");

        audio.Dispose();
    }

    // --------------------------------------------------------------- Recording

    [RelayCommand]
    private async Task StartRecordingAsync()
    {
        if (_ffmpeg is null || !IsIdle) return;

        try
        {
            Directory.CreateDirectory(TargetDirectory);

            // Matroska: the file stays playable at any moment. An MP4 is only complete when closed;
            // if the stream breaks, the recording is gone.
            if (!await PreflightAsync()) return;

            _currentFile = Path.Combine(TargetDirectory, $"Recording {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mkv");
            _bufferMode = false;

            Log.Clear();
            ResetCounters();

            // Via Task.Run: Process.Start can cost hundreds of milliseconds on the first ffmpeg start
            // (antivirus), which mustn't sit in the "Start recording" click.
            var arguments = Capture.ForRecording(BuildSettings(), _currentFile);
            await Task.Run(() => _recorder.Start(_ffmpeg, arguments));

            IsRecording = true;
            _clock.Restart();
            _timer.Start();

            await AttachSystemAudioAsync();
            Show($"Recording: {Path.GetFileName(_currentFile)}");
            Save();
        }
        catch (Exception ex)
        {
            // BuildSettings already opened system audio (endpoint and pipe). If the start failed, it
            // would hold both until the next run.
            if (!IsRecording) ReleaseSystemAudio();
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task StopRecordingAsync()
    {
        if (!IsRecording) return;

        _stopping = true;
        IsWorking = true;

        try
        {
            // Stop ffmpeg first, then audio: while the stream runs ffmpeg doesn't hang finishing on
            // an input that delivers nothing. The other way round, the last moment of audio is lost.
            await _recorder.StopAsync();
        }
        finally
        {
            // Without finally an exception would leave IsWorking set and block the module for good:
            // every button depends on it.
            ReleaseSystemAudio();

            IsWorking = false;
            IsRecording = false;
            _stopping = false;
            _timer.Stop();
            _clock.Stop();
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }

        if (_currentFile is not null && File.Exists(_currentFile))
        {
            var size = Humanize.Bytes(new FileInfo(_currentFile).Length);
            Show($"{Path.GetFileName(_currentFile)} — {size}");
            PublishStatus();

            await ReportAudioAsync(_currentFile, $"{Path.GetFileName(_currentFile)} — {size}");
        }
        else
        {
            Show("No file was created. ffmpeg's output is below.", error: true);
        }

        RefreshRecent();
    }

    // --------------------------------------------------------------- Hotkey

    /// <summary>Registers the hotkey while the buffer runs.</summary>
    /// <remarks>
    /// <para>
    /// <b>Only while the buffer runs.</b> Windows hands out a combination exclusively; a module holding it permanently
    /// takes it from every other program even when it has no use for it.
    /// </para>
    /// <para>
    /// It also fits the house rule: nothing runs without a visible tab. The buffer runs while the tab is open, and
    /// so does the key.
    /// </para>
    /// </remarks>
    private void EnableHotkey()
    {
        var choice = SelectedHotkey;

        if (_hotkey.Start(choice))
        {
            HotkeyText = $"{choice.Label} saves a clip, even with WinTo in the background.";
            return;
        }

        HotkeyText = $"{choice.Label} is taken by another program. Choose another combination.";
        _context.Log.Warn($"Hotkey {choice.Label} could not be registered.");
    }

    private void DisableHotkey()
    {
        _hotkey.Stop();
        HotkeyText = "";
    }

    /// <summary>Comes from the hotkey thread, so it must move to the UI thread.</summary>
    /// <remarks>
    /// <see cref="SaveClipCommand"/> touches an <c>ObservableCollection</c> and bound properties; from another thread
    /// that would crash somewhere in layout, far from the cause.
    /// </remarks>
    private void OnHotkeyPressed() => Dispatcher.UIThread.Post(() =>
    {
        if (!CanSaveClip || IsWorking) return;

        SaveClipCommand.Execute(null);
    });

    // --------------------------------------------------------------- Buffer

    private string BufferDirectory => Path.Combine(_context.DataDirectory, "buffer");

    /// <summary>Segments are in the ring, even if it isn't running.</summary>
    [ObservableProperty] public partial bool HasBufferedSegments { get; set; }

    /// <summary>Whether a clip can be saved.</summary>
    /// <remarks>
    /// <b>Also after stopping.</b> Before, this depended only on <c>IsBuffering</c>: segments stayed on disk after
    /// stopping but were unreachable. Stopping the buffer by accident shouldn't lose the last minutes.
    /// </remarks>
    public bool CanSaveClip => IsBuffering || HasBufferedSegments;

    partial void OnHasBufferedSegmentsChanged(bool value) => OnPropertyChanged(nameof(CanSaveClip));

    private void RefreshBufferState()
    {
        try
        {
            HasBufferedSegments = Directory.Exists(BufferDirectory)
                && Directory.EnumerateFiles(BufferDirectory, "buffer*.mkv").Any(p => new FileInfo(p).Length > 0);
        }
        catch (Exception)
        {
            HasBufferedSegments = false;
        }
    }

    /// <summary>Clears the ring buffer.</summary>
    /// <remarks>
    /// <b>Belongs in disposal, not only the next start.</b> A five-minute ring at 1440p is quickly several hundred
    /// megabytes, which used to stay under <c>%APPDATA%</c> indefinitely.
    /// </remarks>
    private void ClearBuffer()
    {
        try
        {
            if (!Directory.Exists(BufferDirectory)) return;

            foreach (var file in Directory.EnumerateFiles(BufferDirectory))
            {
                try { File.Delete(file); } catch (Exception) { }
            }
        }
        catch (Exception)
        {
            // A segment ffmpeg still holds open stays and is removed at the next buffer start.
        }

        HasBufferedSegments = false;
    }

    [RelayCommand]
    private async Task StartBufferAsync()
    {
        if (_ffmpeg is null || !IsIdle) return;

        try
        {
            if (!await PreflightAsync()) return;

            var directory = BufferDirectory;
            Directory.CreateDirectory(directory);

            // Remove leftovers of a previous run, or old segments mix into the next clip. The whole
            // folder, not just buffer*.mkv: clip.tmp.mkv and clip.txt from an aborted save would
            // otherwise stay forever.
            await Task.Run(ClearBuffer);

            // Two segments spare: one being written, one the ring overwrites next.
            var wrap = (BufferMinutes * 60 / SegmentSeconds) + 2;

            _bufferMode = true;
            _currentFile = null;
            Log.Clear();
            ResetCounters();

            var arguments = Capture.ForBuffer(BuildSettings(), directory, SegmentSeconds, KeyframeSeconds, wrap);
            await Task.Run(() => _recorder.Start(_ffmpeg, arguments));

            IsBuffering = true;
            HasBufferedSegments = false;
            _clock.Restart();
            _timer.Start();
            EnableHotkey();

            await AttachSystemAudioAsync();
            Show(BufferMinutes == 1
                ? "Buffer running. The last minute can be saved at any time."
                : $"Buffer running. The last {BufferMinutes} minutes can be saved at any time.");
            Save();
        }
        catch (Exception ex)
        {
            // As with recording: system audio is already open, ffmpeg isn't.
            if (!IsBuffering) ReleaseSystemAudio();
            Show(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private async Task StopBufferAsync()
    {
        if (!IsBuffering) return;

        _stopping = true;
        IsWorking = true;

        try
        {
            await _recorder.StopAsync();
        }
        finally
        {
            ReleaseSystemAudio();

            IsWorking = false;
            IsBuffering = false;
            _stopping = false;
            _timer.Stop();
            _clock.Stop();
            DisableHotkey();
            ActivityChanged?.Invoke(this, ModuleActivity.Idle);
        }

        // Segments stay and can still be saved; stopping by accident shouldn't lose the last
        // minutes. They're cleared at the next buffer start and when the module is disposed.
        RefreshBufferState();

        Show(CanSaveClip
            ? "Buffer stopped. The last minutes can still be saved."
            : "Buffer stopped.");
    }

    /// <summary>Finishes the current segment, moves the segments aside and keeps buffering.</summary>
    /// <remarks>
    /// <para>
    /// <b>Without this the clip misses the last seconds before the trigger.</b> The segment muxer only closes a
    /// segment when it's full; until then the file has no readable packets. Measured with 15 s segments: <b>5.5 to
    /// 15.6 s</b> missing depending on timing, exactly those right before the key press.
    /// </para>
    /// <para>
    /// A <c>q</c> to ffmpeg closes the segment properly; nothing is missing afterwards (measured −0.1 s). The cost is
    /// a gap of about a second <i>after</i> clipping while ffmpeg restarts. The right trade.
    /// </para>
    /// <para>
    /// Renamed, not copied: <see cref="Directory.Move"/> on the same disk only changes the directory entry and takes
    /// milliseconds. Copying would extend the gap by the duration of a whole ring.
    /// </para>
    /// </remarks>
    /// <returns>The folder with the finished segments.</returns>
    private async Task<string> HarvestAsync()
    {
        // If the buffer is stopped, everything is finished and there's nothing to do.
        if (!IsBuffering) return BufferDirectory;

        var harvest = Path.Combine(
            Path.GetDirectoryName(BufferDirectory)!, $"harvest-{DateTime.Now:HHmmssfff}");

        _stopping = true;

        try
        {
            await _recorder.StopAsync();
            ReleaseSystemAudio();

            Directory.Move(BufferDirectory, harvest);
            Directory.CreateDirectory(BufferDirectory);
        }
        catch (Exception ex)
        {
            // If the folder couldn't be moved, save from the running one; the last seconds are missing
            // again, but there's a clip.
            _context.Log.Warn($"Buffer could not be finished: {ex.Message}");
            _stopping = false;

            return Directory.Exists(harvest) ? harvest : BufferDirectory;
        }
        finally
        {
            _stopping = false;
        }

        await RestartBufferAsync();
        return harvest;
    }

    /// <summary>Restarts the ring without repeating the preflight.</summary>
    /// <remarks>The encoder was just working; probing it again would cost time in which nothing is recorded.</remarks>
    private async Task RestartBufferAsync()
    {
        try
        {
            // The ring starts over, so does the display. Otherwise the old time shows until the new
            // ffmpeg's first status line: reset and notify before anyone looks.
            ResetCounters();

            var wrap = (BufferMinutes * 60 / SegmentSeconds) + 2;
            var arguments = Capture.ForBuffer(
                BuildSettings(), BufferDirectory, SegmentSeconds, KeyframeSeconds, wrap);

            await Task.Run(() => _recorder.Start(_ffmpeg!, arguments));

            IsBuffering = true;
            HasBufferedSegments = false;
            _clock.Restart();

            await AttachSystemAudioAsync();
        }
        catch (Exception ex)
        {
            IsBuffering = false;
            _timer.Stop();
            _clock.Stop();
            DisableHotkey();
            ReleaseSystemAudio();

            Log.Add($"Buffer did not resume after saving: {ex.Message}");
        }
    }

    /// <summary>Saves the last minutes from the ring buffer.</summary>
    /// <remarks>
    /// Two steps in mandatory order: join the segments, then cut from the end. <c>-sseof</c> counts backwards and
    /// needs a known total duration, which a file still being written doesn't have.
    /// </remarks>
    [RelayCommand]
    private async Task SaveClipAsync()
    {
        if (_ffmpeg is null || IsWorking || !CanSaveClip) return;

        IsWorking = true;

        // Finish the current segment first, or the clip misses the seconds before the key press.
        var harvest = await HarvestAsync();
        var ownFolder = !string.Equals(harvest, BufferDirectory, StringComparison.OrdinalIgnoreCase);

        var temporary = Path.Combine(harvest, "clip.tmp.mkv");
        var listFile = Path.Combine(harvest, "clip.txt");

        try
        {
            Directory.CreateDirectory(TargetDirectory);

            var segments = Directory
                .EnumerateFiles(harvest, "buffer*.mkv")
                .Select(path => new FileInfo(path))
                .Where(file => file.Length > 0)
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();

            if (segments.Count == 0)
            {
                Show("The buffer is still empty. A segment takes "
                    + $"{SegmentSeconds} seconds.", error: true);
                return;
            }

            // Segments run in a circle and carry no ascending numbers. Write time is the only
            // reliable order.
            var wanted = (BufferMinutes * 60 / SegmentSeconds) + 1;
            var take = segments.TakeLast(wanted).ToList();

            var target = Path.Combine(TargetDirectory, $"Clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mkv");

            var problem = await ConcatAsync(take, listFile, temporary);

            // The most recently written segment is the one ffmpeg is writing. If it's locked the last
            // seconds are missing; better than no clip.
            if (problem is not null && take.Count > 1)
            {
                Log.Add("Current segment unreadable. Clip without the last seconds.");
                problem = await ConcatAsync(take.SkipLast(1).ToList(), listFile, temporary);
            }

            if (problem is not null)
            {
                Show($"Joining failed: {problem}", error: true);
                return;
            }

            var tail = await ExternalProcess.RunAsync(_ffmpeg,
                Capture.ForTail(temporary, BufferMinutes * 60, target),
                cancellationToken: _context.Shutdown);

            if (!tail.Ok)
            {
                // Trimming is the finishing touch. If it fails, the joined clip is still usable, just longer.
                Log.Add("Trimming failed. The clip contains the whole buffer.");
                File.Move(temporary, target, overwrite: true);
            }

            var headline = $"{Path.GetFileName(target)} — {Humanize.Bytes(new FileInfo(target).Length)}";

            Show(headline);
            PublishStatus();
            RefreshRecent();

            await ReportAudioAsync(target, headline);
        }
        catch (Exception ex)
        {
            Show(ex.Message, error: true);
        }
        finally
        {
            foreach (var path in new[] { temporary, listFile })
            {
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
            }

            // The harvest folder has served its purpose. Leaving it would forget several hundred
            // megabytes in the data folder with every clip.
            if (ownFolder)
            {
                try { Directory.Delete(harvest, recursive: true); }
                catch (Exception ex) { _context.Log.Warn($"Harvest folder left behind: {ex.Message}"); }
            }

            IsWorking = false;
        }
    }

    private async Task<string?> ConcatAsync(IReadOnlyList<FileInfo> segments, string listFile, string output)
    {
        if (segments.Count == 0) return "No segment left.";

        // The concat demuxer reads a text file. Single quotes in paths must be escaped there; a
        // folder name with an apostrophe is enough.
        //
        // Explicitly WITHOUT a BOM: with one the demuxer reads the first line as "﻿file" and
        // aborts with "unknown keyword". The opposite of the rule for log files, where PowerShell
        // needs the BOM.
        await File.WriteAllLinesAsync(listFile,
            segments.Select(s => $"file '{s.FullName.Replace("'", "'\\''")}'"),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            _context.Shutdown);

        var result = await ExternalProcess.RunAsync(_ffmpeg!,
            Capture.ForConcat(listFile, output), cancellationToken: _context.Shutdown);

        return result.Ok ? null : result.Problem;
    }

    /// <summary>Checks whether the finished file has an audio track at all.</summary>
    /// <remarks>
    /// <para>
    /// <b>Presence is measured, not level.</b> An earlier version reported every quiet clip as an error, which it
    /// isn't: saving a quiet moment from the buffer yields exactly that. A red box for a normal case turns every
    /// real alarm into a line you dismiss.
    /// </para>
    /// <para>
    /// The original case (loopback delivered nothing because nothing played on the endpoint) is fixed at the root.
    /// What remains is the one question a file can't answer itself: was an audio source chosen and no track arrived.
    /// </para>
    /// </remarks>
    private async Task ReportAudioAsync(string path, string head)
    {
        if (_ffmpeg is null) return;

        // Without a chosen source a missing track is intended and not worth a message.
        var hasSource = SystemAudioEnabled || AudioChoices.Any(c => c.Selected);
        if (!hasSource) return;

        var level = await Capture.MeasureAudioAsync(_ffmpeg, path, _context.Shutdown);
        if (level.HasStream) return;

        // The file is saved and plays; the message above stays a success.
        Hint = "No sound in the file. The selected audio source delivered none.";
    }

    // --------------------------------------------------------------- Display

    /// <summary>Already reported that the computer can't keep up.</summary>
    private bool _overloadReported;

    private void OnRecorderLine(string line)
    {
        // The dshow input drops once the encoder is too slow. Without this branch the message
        // would only be in the log nobody opens, and a recording that produces nothing looks like a hang.
        if (!_overloadReported && Capture.LooksLikeOverload(line))
        {
            _overloadReported = true;

            var (width, height) = CaptureSize;

            Dispatcher.UIThread.Post(() => Show(
                $"Frames are being dropped: {width}×{height} with "
                + $"{SelectedEncoder?.Label ?? ""} is too much. "
                + "Lower the frame rate or area.", error: true));
        }

        if (Capture.ReadStats(line) is { } stats)
        {
            Dispatcher.UIThread.Post(() =>
            {
                SizeText = stats.Bytes > 0 ? Humanize.Bytes(stats.Bytes) : "";

                ActivityChanged?.Invoke(this, new ModuleActivity(
                    true, _bufferMode ? $"Buffer running · {ElapsedText}" : $"Recording · {ElapsedText}"));
            });

            return;
        }

        if (Capture.IsProgress(line)) return;

        Dispatcher.UIThread.Post(() =>
        {
            Log.Add(line);
            while (Log.Count > 100) Log.RemoveAt(0);
        });
    }

    /// <summary>Set while an <b>intended</b> end is in progress.</summary>
    /// <remarks>
    /// <c>Exited</c> also fires when the end was explicitly requested: the <c>q</c> goes out, ffmpeg exits, and the
    /// callback meets an <c>IsRecording</c> that is reset only afterwards. Without this the module reported "ffmpeg
    /// exited" after every clean stop, and worse: with a nonzero exit code and an encoder-like line in the log, the
    /// fallback restarted the recording right away.
    /// </remarks>
    private bool _stopping;

    private void OnRecorderExited(int code) => Dispatcher.UIThread.Post(async () =>
    {
        // Belt and braces: since the recorder no longer reports requested ends (see
        // _deliberateStop there), _stopping only covers the rest of the same window.
        if (_stopping) return;
        if (!IsRecording && !IsBuffering) return;

        // ffmpeg left on its own: no device access, disk full, a wrong device name. The reason is
        // in the last log lines.
        var wasBuffer = _bufferMode;

        IsRecording = false;
        IsBuffering = false;
        _timer.Stop();
        _clock.Stop();
        DisableHotkey();

        // ffmpeg is gone; the pipe has nobody at the other end. Otherwise the loopback capture
        // would keep running and hold the endpoint, never freed since the load context isn't unloaded.
        ReleaseSystemAudio();

        ActivityChanged?.Invoke(this, ModuleActivity.Idle);

        // The hardware encoder refused although it passed the preflight: a GPU taken by a game
        // meanwhile, a driver that just reloaded. Instead of leaving an empty file, switch to the
        // CPU and start the same run again. Exactly once.
        if (code != 0
            && SelectedEncoder?.Id != "libx264"
            && Capture.LooksLikeEncoderProblem(Log))
        {
            var refused = SelectedEncoder;
            SelectedEncoder = SoftwareEncoder() ?? SelectedEncoder;

            TryDeleteEmpty(_currentFile);

            if (wasBuffer) await StartBufferAsync();
            else await StartRecordingAsync();

            // Only now: the restart clears the log and sets its own message. Before, the reason showed
            // for a fraction of a second and was gone; the user only saw the recording suddenly running
            // on the CPU.
            Show($"{refused?.Label ?? ""} refused, recording continues "
                + "on the CPU. The setting is saved.", error: false);

            return;
        }

        if (code != 0) TryDeleteEmpty(_currentFile);

        Show(code == 0
            ? "ffmpeg exited."
            : $"ffmpeg aborted (exit code {code}). Its output is below.", error: code != 0);

        RefreshRecent();
    });

    /// <summary>Removes the 0-byte file a failed start leaves behind.</summary>
    private static void TryDeleteEmpty(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path) && new FileInfo(path).Length == 0)
                File.Delete(path);
        }
        catch (Exception)
        {
            // A leftover empty file is annoying, nothing more.
        }
    }

    private void RefreshRecent()
    {
        Recent.Clear();

        try
        {
            if (!Directory.Exists(TargetDirectory)) return;

            foreach (var file in new DirectoryInfo(TargetDirectory)
                         .EnumerateFiles("*.mkv")
                         .Concat(new DirectoryInfo(TargetDirectory).EnumerateFiles("*.mp4"))
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Take(12))
            {
                Recent.Add($"{file.Name}   ·   {Humanize.Bytes(file.Length)}   ·   {Humanize.Ago(file.LastWriteTime)}");
            }
        }
        catch (Exception)
        {
            // An unreadable output folder costs the list, nothing else.
        }
    }

    private void PublishStatus()
    {
        var count = Directory.Exists(TargetDirectory)
            ? Directory.EnumerateFiles(TargetDirectory, "*.mkv").Count()
              + Directory.EnumerateFiles(TargetDirectory, "*.mp4").Count()
            : 0;

        _context.PublishStatus(new ModuleStatus(
            StatusLevel.Good, $"{count} recordings", DateTimeOffset.Now, TargetDirectory));
    }

    // --------------------------------------------------------------- Helpers

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (await _context.Shell.PickFolderAsync("Output folder") is { } folder)
        {
            TargetDirectory = folder;
            Save();
            RefreshRecent();
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(TargetDirectory);
        _context.Shell.RevealInExplorer(TargetDirectory);
    }

    [RelayCommand]
    private async Task RefreshDevicesAsync() => await PrepareAsync(refreshEncoders: true);

    public void StartTicking()
    {
        if (IsRecording || IsBuffering) _timer.Start();
    }

    public void StopTicking() => _timer.Stop();

    partial void OnFramesPerSecondChanged(int value) => Save();
    partial void OnQualityChanged(int value) => Save();
    partial void OnCaptureCursorChanged(bool value) => Save();
    partial void OnBufferMinutesChanged(int value) => Save();
    partial void OnSelectedAreaChanged(CaptureArea? value) => Save();

    private void Save()
    {
        if (_loading) return;

        _context.Settings.Save(new RecorderSettings
        {
            TargetDirectory = TargetDirectory,
            FramesPerSecond = FramesPerSecond,
            Quality = Quality,
            CaptureCursor = CaptureCursor,
            BufferMinutes = BufferMinutes,
            AreaName = SelectedArea?.Name ?? "",
            Encoder = SelectedEncoder?.Id ?? "",
            AudioDevices = [.. AudioChoices.Where(c => c.Selected).Select(c => c.Name)],
            SystemAudio = SystemAudioEnabled,

            // Empty for the default device: someone switching headphones wants to hear the new ones,
            // not a device no longer connected. Empty means Follows. An explicitly chosen device is
            // remembered, the default device itself isn't; otherwise a fixed id would appear where
            // "follows Windows" was meant after the next switch.
            SystemAudioDeviceId = SelectedRenderDevice is { IsFollows: false, IsDefault: false } chosen
                ? chosen.Id
                : "",
            ClipHotkey = SelectedHotkey.Id,
            LevelMatch = LevelMatch,
            DeviceGainDb = DeviceGainDb,
            SystemGainDb = SystemGainDb,
        });
    }

    private void Show(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
        Hint = null;

        // A failure usually has its reason in ffmpeg's last lines.
        if (error && HasLog) ShowLog = true;
    }

    public void Dispose()
    {
        _timer.Stop();

        _hotkey.Pressed -= OnHotkeyPressed;
        _recorder.Line -= OnRecorderLine;
        _recorder.Exited -= OnRecorderExited;

        // The fast part right away: the request to ffmpeg costs nothing and is out even if the
        // app ends right after this call.
        _recorder.RequestStop();

        // The waiting part on a background thread. This call comes from the close prompt on the
        // UI thread, and the chain below may wait: the hotkey thread up to 2 s in Join, ffmpeg up
        // to 1.5 s in its deadline, plus deleting segments. A frozen window looks like a crash.
        // The view is already detached here (ViewClosed sets the DataContext to null).
        //
        // A registered combination must still be returned, and the WASAPI capture holds a COM
        // endpoint and a named pipe open: Dispose is the only release there is, since the
        // module's load context is never collected.
        void Release()
        {
            _hotkey.Dispose();
            _recorder.Dispose();
            ReleaseSystemAudio();
            ClearBuffer();
        }

        // When the whole app exits, though, here and now: ffmpeg is tied to the app's lifetime
        // (ExternalProcess.TieToApplication). If waiting continued in the background, the process
        // would end first and Windows would kill ffmpeg mid-trailer. 1.5 s of waiting after a
        // confirmed "Quit" beats a file without an index.
        if (_context.Shutdown.IsCancellationRequested) Release();
        else _ = Task.Run(Release);
    }
}
