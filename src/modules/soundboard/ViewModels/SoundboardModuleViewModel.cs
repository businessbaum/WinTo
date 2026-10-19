using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Modules.Soundboard.Audio;
using MultiTool.Modules.Soundboard.Models;
using MultiTool.Modules.Soundboard.Services;

namespace MultiTool.Modules.Soundboard.ViewModels;

public enum StatusKind
{
    Good,
    Warning,
    Bad,
}

public partial class SoundboardModuleViewModel : ObservableObject
{
    /// <summary>Playback display tick. Fine enough for a smooth bar, coarse enough to cost nothing.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(120);

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;

    private bool _suppressDefaultMicWrite;
    private bool _suppressAudioAppWrite;

    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _statusText = "Starting…";
    [ObservableProperty] private StatusKind _statusKind = StatusKind.Warning;
    [ObservableProperty] private string _statusDetail = string.Empty;

    // --- playback bar ---
    [ObservableProperty] private bool _hasPlayback;
    [ObservableProperty] private string _nowPlayingName = string.Empty;
    [ObservableProperty] private double _nowPlayingProgress;
    [ObservableProperty] private string _nowPlayingTime = string.Empty;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _nowPlayingLooping;
    [ObservableProperty] private string _extraPlaybackText = string.Empty;
    [ObservableProperty] private bool _hasExtraPlayback;

    // --- Windows default microphone ---
    [ObservableProperty] private AudioDevice? _selectedDefaultMic;
    [ObservableProperty] private string? _defaultMicMessage;

    // --- desktop audio ---
    [ObservableProperty] private bool _desktopTunnelActive;
    [ObservableProperty] private string? _desktopTunnelNote;
    [ObservableProperty] private AudioApp? _selectedAudioApp;

    private Guid _currentSoundId;

    public SoundboardModuleViewModel(AppServices services, IDialogService dialogs)
    {
        _services = services;

        Soundboard = new SoundboardViewModel(services, dialogs, RefreshHotkeyBindings);
        Settings = new SettingsViewModel(services, dialogs, RefreshHotkeyBindings, UpdateStatus);

        _services.Engine.SoundStateChanged += OnSoundStateChanged;
        _services.Engine.RoutesChanged += () => Dispatcher.UIThread.Post(UpdateStatus);
        _services.Engine.DesktopTunnelChanged += () => Dispatcher.UIThread.Post(RefreshDesktopTunnel);

        // The default microphone is requeried when Windows reports a device change (until 0.9.0
        // every three seconds on suspicion, about 100 ms of COM time each).
        _services.Engine.DevicesChanged += OnDevicesChanged;

        _timer = new DispatcherTimer(TickInterval, DispatcherPriority.Background, OnTick);
    }

    public SoundboardViewModel Soundboard { get; }

    public SettingsViewModel Settings { get; }

    public bool IsStatusGood => StatusKind == StatusKind.Good;
    public bool IsStatusWarning => StatusKind == StatusKind.Warning;
    public bool IsStatusBad => StatusKind == StatusKind.Bad;

    partial void OnStatusKindChanged(StatusKind value)
    {
        OnPropertyChanged(nameof(IsStatusGood));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusBad));
    }

    // ================= Levels =================

    /// <summary>Sound level. Affects only the sounds; the microphone sits after this slider in the graph.</summary>
    public double MasterVolume
    {
        get => _services.Settings.MasterVolume;
        set
        {
            var clamped = (float)Math.Clamp(value, 0d, 1d);
            if (Math.Abs(clamped - _services.Settings.MasterVolume) < 0.001f) return;

            _services.Settings.MasterVolume = clamped;
            ApplyLevels();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MasterVolumeText));
            OnPropertyChanged(nameof(MasterVolumeTip));
        }
    }

    public string MasterVolumeText => $"{_services.Settings.MasterVolume * 100:0}%";

    public string MasterVolumeTip =>
        $"Sound volume · {VolumeCurve.Describe(_services.Settings.MasterVolume)}\n" +
        "Doesn't affect the microphone.";

    /// <summary>Microphone level. Independent of <see cref="MasterVolume"/>.</summary>
    public double MicVolume
    {
        get => _services.Settings.MicVolume;
        set
        {
            var clamped = (float)Math.Clamp(value, 0d, 2d);
            if (Math.Abs(clamped - _services.Settings.MicVolume) < 0.001f) return;

            _services.Settings.MicVolume = clamped;
            ApplyLevels();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MicVolumeText));
            OnPropertyChanged(nameof(MicVolumeTip));
        }
    }

    public string MicVolumeText => $"{_services.Settings.MicVolume * 100:0}%";

    public string MicVolumeTip =>
        $"Microphone volume · {VolumeCurve.Describe(_services.Settings.MicVolume)}\n" +
        "Doesn't affect the sounds.";

    public bool MicSliderEnabled => _services.Settings.MicPassthroughEnabled;

    /// <summary>Desktop audio level. Tied to none of the other sliders.</summary>
    public double DesktopVolume
    {
        get => _services.Settings.DesktopVolume;
        set
        {
            var clamped = (float)Math.Clamp(value, 0d, 2d);
            if (Math.Abs(clamped - _services.Settings.DesktopVolume) < 0.001f) return;

            _services.Settings.DesktopVolume = clamped;
            ApplyLevels();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DesktopVolumeText));
            OnPropertyChanged(nameof(DesktopVolumeTip));
        }
    }

    public string DesktopVolumeText => $"{_services.Settings.DesktopVolume * 100:0}%";

    public string DesktopVolumeTip =>
        $"Desktop audio volume in the cable · {VolumeCurve.Describe(_services.Settings.DesktopVolume)}\n" +
        "Doesn't change your own monitoring.";

    private void ApplyLevels()
    {
        _services.Engine.Apply(_services.Settings);
        _services.SaveSettingsSoon();
        Settings.NotifyLevelsChanged();
    }

    // ================= Windows default microphone =================

    public ObservableCollection<AudioDevice> DefaultMicDevices { get; } = [];

    /// <summary>Without the undocumented interface the picker stays hidden.</summary>
    public bool DefaultMicSupported { get; } = DefaultDeviceSwitcher.IsSupported;

    partial void OnSelectedDefaultMicChanged(AudioDevice? value)
    {
        if (_suppressDefaultMicWrite || value is null) return;

        if (DefaultDeviceSwitcher.TrySetDefaultCaptureDevice(value.Id, out var error))
        {
            DefaultMicMessage = null;
            // Our own "system default" assignments now point elsewhere.
            _services.Engine.Apply(_services.Settings);
            UpdateStatus();
        }
        else
        {
            DefaultMicMessage = error;
        }
    }

    private bool _defaultMicRefreshRunning;

    /// <summary>Queries on a background thread, then displays on the UI thread.</summary>
    /// <remarks>
    /// Same split as <see cref="StartAudioAsync"/>, for the same reason: the two COM calls cost about 100 ms
    /// together, a regular stutter if run on the UI thread.
    /// </remarks>
    private void RefreshDefaultMic()
    {
        if (_defaultMicRefreshRunning) return;
        _defaultMicRefreshRunning = true;

        _ = Task.Run(() =>
        {
            try
            {
                var devices = AudioDevices.Capture();
                var currentId = DefaultDeviceSwitcher.GetDefaultCaptureDeviceId();
                Dispatcher.UIThread.Post(() =>
                {
                    _defaultMicRefreshRunning = false;
                    ApplyDefaultMic(devices, currentId);
                });
            }
            catch (Exception)
            {
                // Without an answer the display keeps its last state; the next query tries again.
                Dispatcher.UIThread.Post(() => _defaultMicRefreshRunning = false);
            }
        });
    }

    /// <summary>Display only. The query costs about 100 ms of COM time.</summary>
    private void ApplyDefaultMic(List<AudioDevice> devices, string? currentId)
    {
        var sameList = devices.Count == DefaultMicDevices.Count &&
                       devices.Select(d => d.Id).SequenceEqual(DefaultMicDevices.Select(d => d.Id));

        _suppressDefaultMicWrite = true;
        try
        {
            if (!sameList)
            {
                DefaultMicDevices.Clear();
                foreach (var device in devices) DefaultMicDevices.Add(device);
            }

            var match = DefaultMicDevices.FirstOrDefault(d => d.Id == currentId);
            if (!ReferenceEquals(match, SelectedDefaultMic)) SelectedDefaultMic = match;
        }
        finally
        {
            _suppressDefaultMicWrite = false;
        }
    }

    // ================= Desktop audio =================

    /// <summary>
    /// Routes the running system audio into the virtual cable. Meant as a short tunnel: make music, video or
    /// game audio audible to others, then close it again.
    /// </summary>
    [RelayCommand]
    private void ToggleDesktopTunnel() =>
        _services.Triggers.Post(() => _services.Engine.ToggleDesktopTunnel());

    private void RefreshDesktopTunnel()
    {
        var engine = _services.Engine;

        DesktopTunnelActive = engine.DesktopTunnelActive;
        DesktopTunnelNote = engine.DesktopTunnelNote;

        OnPropertyChanged(nameof(DesktopTunnelLabel));
        OnPropertyChanged(nameof(DesktopTunnelTip));
    }

    // --- app selection ---

    public ObservableCollection<AudioApp> AudioApps { get; } = [];

    /// <summary>
    /// Rereads the running apps. Called when the list drops down, not on the display tick: the query walks all
    /// processes and audio sessions, and the result only matters in that moment. A short pause is fine there;
    /// the user just asked for it.
    /// </summary>
    public void RefreshAudioApps() => ApplyAudioApps(RunningApps.List());

    /// <summary>Display only. The query costs about 80 ms of COM time.</summary>
    private void ApplyAudioApps(List<AudioApp> apps)
    {
        var stored = _services.Settings.DesktopTunnelApp;

        var list = new List<AudioApp>(apps.Count + 2) { AudioApp.WholeDesktop };
        list.AddRange(apps);

        // The remembered app stays selectable even when not running; otherwise the selection
        // would silently jump back when the list drops down.
        if (!string.IsNullOrWhiteSpace(stored) &&
            !list.Any(a => string.Equals(a.Key, stored, StringComparison.OrdinalIgnoreCase)))
        {
            list.Insert(1, new AudioApp(stored, $"{stored} (not running)", 0, false));
        }

        var unchanged = list.Count == AudioApps.Count &&
                        list.Select(a => a.Key).SequenceEqual(AudioApps.Select(a => a.Key));

        _suppressAudioAppWrite = true;
        try
        {
            if (!unchanged)
            {
                AudioApps.Clear();
                foreach (var app in list) AudioApps.Add(app);
            }

            var match = AudioApps.FirstOrDefault(
                            a => string.Equals(a.Key, stored ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                        ?? AudioApps.FirstOrDefault();

            if (!ReferenceEquals(match, SelectedAudioApp)) SelectedAudioApp = match;
        }
        finally
        {
            _suppressAudioAppWrite = false;
        }
    }

    partial void OnSelectedAudioAppChanged(AudioApp? value)
    {
        if (_suppressAudioAppWrite || value is null) return;

        var key = value.IsWholeDesktop ? null : value.Key;
        if (key == _services.Settings.DesktopTunnelApp) return;

        _services.Settings.DesktopTunnelApp = key;

        // If the tunnel runs, the engine switches it to the new app.
        _services.Engine.Apply(_services.Settings);
        _services.SaveSettingsSoon();

        OnPropertyChanged(nameof(DesktopTunnelLabel));
        OnPropertyChanged(nameof(DesktopTunnelTip));
    }

    public string DesktopTunnelLabel => DesktopTunnelActive ? "Audio on" : "Send audio";

    public string DesktopTunnelTip
    {
        get
        {
            var hotkey = _services.Settings.DesktopTunnelHotkey is { IsEmpty: false } key
                ? $"\nHotkey: {key.DisplayName}"
                : "\nHotkey can be set in settings.";

            if (DesktopTunnelActive)
                return (DesktopTunnelNote ?? "Audio is going into the cable.") + "\n\nClick to stop." + hotkey;

            var reason = DesktopTunnelNote is null ? string.Empty : $"\n\n{DesktopTunnelNote}";

            var what = SelectedAudioApp is { IsWholeDesktop: false } app
                ? $"the audio of \"{app.Name}\""
                : "all system audio";

            return $"Routes {what} into the virtual cable. " +
                   "The picker on the right selects the app." + reason + hotkey;
        }
    }

    partial void OnDesktopTunnelActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(DesktopTunnelLabel));
        OnPropertyChanged(nameof(DesktopTunnelTip));
    }

    // ================= Playback bar =================

    private void OnTick(object? sender, EventArgs e)
    {
        var status = _services.Engine.GetStatus();

        if (status is null)
        {
            if (HasPlayback)
            {
                HasPlayback = false;
                HasExtraPlayback = false;
                NowPlayingProgress = 0;
                _currentSoundId = Guid.Empty;
                Settings.ResetLimiter();
            }
        }
        else
        {
            _currentSoundId = status.SoundId;
            HasPlayback = true;
            NowPlayingName = status.Name;
            IsPaused = status.IsPaused;
            NowPlayingLooping = status.IsLooping;

            NowPlayingProgress = status.Duration > TimeSpan.Zero
                ? Math.Clamp(status.Position.TotalSeconds / status.Duration.TotalSeconds, 0, 1)
                : 0;

            NowPlayingTime = status.Duration > TimeSpan.Zero
                ? $"{Format(status.Position)} / {Format(status.Duration)}"
                : Format(status.Position);

            HasExtraPlayback = status.ActiveCount > 1;
            ExtraPlaybackText = status.ActiveCount > 1 ? $"+{status.ActiveCount - 1}" : string.Empty;
        }

        // The clipping protection only reports when it actually engages.
        if (HasPlayback) Settings.PollLimiter();

    }

    /// <summary>Comes from the device notification background thread.</summary>
    private void OnDevicesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (DefaultMicSupported) RefreshDefaultMic();
    });

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    [RelayCommand]
    private void TogglePause()
    {
        if (_currentSoundId == Guid.Empty) return;

        var id = _currentSoundId;
        _services.Triggers.Post(() => _services.Engine.TogglePause(id));
    }

    [RelayCommand]
    private void StopCurrent()
    {
        if (_currentSoundId == Guid.Empty) return;

        var id = _currentSoundId;
        _services.Triggers.Post(() => _services.Engine.Stop(id));
    }

    // ================= Start and state =================

    /// <summary>Gets the module going. Runs inside the user's click and therefore stays short.</summary>
    /// <remarks>
    /// <para>
    /// The keyboard hook needs a thread with a message loop. Since 0.9.0 <see cref="GlobalHotkeyService"/> brings
    /// its own; on the UI thread every UI stutter would have delayed every key press system-wide.
    /// </para>
    /// <para>
    /// The audio graph is deliberately not part of it. The three COM paths cost about 285 ms together (build
    /// devices 104, query default mic 100, list audio apps 80): a quarter second of the tile looking pressed.
    /// So they run in the background, and the header says "Starting…" until done.
    /// </para>
    /// </remarks>
    public void Initialize()
    {
        var settings = _services.Settings;

        _services.Hotkeys.Enabled = settings.HotkeysEnabled;
        _services.Hotkeys.SuppressMatched = settings.SuppressHotkeysInOtherApps;
        var installed = _services.Hotkeys.Install();

        RefreshHotkeyBindings();
        if (!installed) StatusDetail = "Hotkeys could not be registered.";

        _ = Soundboard.WarmUpAsync();
        _ = StartAudioAsync();
    }

    /// <summary>Builds the audio graph and fetches the needed devices and apps, all off the UI thread.</summary>
    /// <remarks>
    /// Allowed because <c>AudioEngine.Apply</c> runs under its own lock and never touches the UI thread. The
    /// result is applied back up here; the <c>await</c> returns to the UI thread, and the collections are bound
    /// to the UI.
    /// </remarks>
    private async Task StartAudioAsync()
    {
        var settings = _services.Settings;
        var wantsMic = DefaultMicSupported;

        var snapshot = await Task.Run(() =>
        {
            _services.Engine.Apply(settings);

            var render = AudioDevices.Render();
            var capture = AudioDevices.Capture();

            return new AudioSnapshot(
                render,
                capture,
                wantsMic ? DefaultDeviceSwitcher.GetDefaultCaptureDeviceId() : null,
                RunningApps.List());
        });

        Settings.ApplyDevices(snapshot.RenderDevices, snapshot.CaptureDevices);
        if (wantsMic) ApplyDefaultMic(snapshot.CaptureDevices, snapshot.DefaultCaptureId);
        ApplyAudioApps(snapshot.RunningApps);
        RefreshDesktopTunnel();
        UpdateStatus();
    }

    /// <summary>What the background run collects. Plain data, nothing observable.</summary>
    private sealed record AudioSnapshot(
        List<AudioDevice> RenderDevices,
        List<AudioDevice> CaptureDevices,
        string? DefaultCaptureId,
        List<AudioApp> RunningApps);

    /// <summary>Starts the display tick. Called once the view is visible.</summary>
    /// <remarks>
    /// Separate from <see cref="Initialize"/> because the lifetimes differ. The audio graph must run while the
    /// module is loaded (voice chat depends on the cable); the display tick draws a progress bar every 120 ms that
    /// nobody watches otherwise.
    /// </remarks>
    public void StartTicking()
    {
        RefreshDesktopTunnel();
        UpdateStatus();
        _timer.Start();

        // Once when becoming visible, as a safety net under the device notifications: if the
        // notifier can't register, the display is at least right when looked at.
        if (DefaultMicSupported) RefreshDefaultMic();
    }

    /// <summary>Stops the display tick. Playback continues.</summary>
    public void StopTicking() => _timer.Stop();

    /// <summary>Cancels a running hotkey capture. Needed when the view closes, or the hook keeps waiting and swallows the next key press.</summary>
    public void CancelHotkeyRecording() => _services.Recorder.Cancel();

    /// <summary>Whether audio is flowing: sounds, microphone into the cable, or desktop audio.</summary>
    public bool IsAudioFlowing =>
        _services.Engine.GetStatus() is not null
        || _services.Engine.MicReachesVirtualOutput
        || _services.Engine.DesktopTunnelActive;

    [RelayCommand]
    private void StopAll() => _services.Triggers.Post(() => _services.Engine.StopAll());

    public void RefreshHotkeyBindings()
    {
        var bindings = new List<(Hotkey, Action)>();

        foreach (var item in Soundboard.All)
        {
            if (item.Entry.Hotkey is not { IsEmpty: false } hotkey) continue;

            var entry = item.Entry;
            bindings.Add((hotkey, () => _services.Triggers.Post(() => _services.Engine.Toggle(entry))));
        }

        if (_services.Settings.StopAllHotkey is { IsEmpty: false } stopAll)
        {
            bindings.Add((stopAll, () => _services.Triggers.Post(() => _services.Engine.StopAll())));
        }

        if (_services.Settings.DesktopTunnelHotkey is { IsEmpty: false } desktop)
        {
            bindings.Add((desktop, () => _services.Triggers.Post(() => _services.Engine.ToggleDesktopTunnel())));
        }

        OnPropertyChanged(nameof(DesktopTunnelTip));

        _services.Hotkeys.SetBindings(bindings);
    }

    private void OnSoundStateChanged(Guid soundId, bool playing) =>
        Dispatcher.UIThread.Post(() => Soundboard.SetPlayingState(soundId, playing));

    /// <summary>Sums up the audio chain's state as a traffic light.</summary>
    public void UpdateStatus()
    {
        var engine = _services.Engine;
        var settings = _services.Settings;

        if (!settings.VirtualOutputEnabled)
        {
            StatusKind = StatusKind.Warning;
            StatusText = "Monitor only";
            StatusDetail = "Virtual output disabled. Other programs hear nothing.";
        }
        else if (!engine.VirtualRouteActive)
        {
            StatusKind = StatusKind.Bad;
            StatusText = "No output";
            StatusDetail = engine.LastError ?? "The playback device could not be opened.";
        }
        else if (engine.VirtualRouteDeviceName is { } name && !VirtualCableDetector.IsVirtualCableDevice(name))
        {
            StatusKind = StatusKind.Warning;
            StatusText = "No virtual cable";
            StatusDetail = $"\"{name}\" is not a virtual cable. Other programs don't receive the audio " +
                           "as a microphone.";
        }
        else
        {
            StatusKind = StatusKind.Good;
            StatusText = engine.MicActive ? "Active · microphone mixed" : "Active";
            StatusDetail = engine.LastError ?? $"Output to {engine.VirtualRouteDeviceName}";
        }

        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(MasterVolumeTip));
        OnPropertyChanged(nameof(MicVolume));
        OnPropertyChanged(nameof(MicVolumeText));
        OnPropertyChanged(nameof(MicVolumeTip));
        OnPropertyChanged(nameof(MicSliderEnabled));
        OnPropertyChanged(nameof(DesktopVolume));
        OnPropertyChanged(nameof(DesktopVolumeText));
        OnPropertyChanged(nameof(DesktopVolumeTip));

        // The settings page shows parts of the engine state too.
        Settings.NotifyEngineStateChanged();
    }

    public void Shutdown()
    {
        Soundboard.CancelWork();
        _timer.Stop();
        _services.Engine.StopAll();
        _services.Engine.SoundStateChanged -= OnSoundStateChanged;
        _services.Engine.DevicesChanged -= OnDevicesChanged;
        _services.Recorder.Cancel();
        _services.Hotkeys.Uninstall();
    }
}
