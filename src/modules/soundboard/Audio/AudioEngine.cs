using NAudio.CoreAudioApi;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using MultiTool.Modules.Soundboard.Models;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>Snapshot of the current playback for display.</summary>
/// <param name="ActiveCount">How many sounds are playing in total.</param>
public sealed record PlaybackStatus(
    Guid SoundId,
    string Name,
    TimeSpan Position,
    TimeSpan Duration,
    bool IsPaused,
    bool IsLooping,
    int ActiveCount);

/// <summary>
/// The app's audio graph.
///
/// <code>
///   Sounds ──► SoundMixer ──► sound level ──┐
///                                           ├──► RouteMixer ──► WASAPI ► CABLE Input
///   Microphone ─────────────► mic level  ───┘                            (Discord hears this)
///
///   Sounds ──► SoundMixer ──► sound level ──► RouteMixer ──► WASAPI ► headphones
/// </code>
///
/// Two things matter:
///
/// First, the <b>two-stage mix</b>. The sound level sits <i>before</i> merging with the microphone. After
/// it, turning sounds up would also make the voice louder.
///
/// Second, <b>separate outputs</b> instead of a splitter: the devices run on their own clocks, and the
/// microphone belongs only in the virtual branch.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly object _activeGate = new();
    private readonly Dictionary<Guid, Playback> _active = [];

    private OutputRoute? _virtualRoute;
    private OutputRoute? _monitorRoute;
    private MicPassthrough? _mic;
    private DesktopTunnel? _desktop;
    private AppSettings _settings = new();
    private bool _micInVirtualRoute;

    /// <summary>Should the desktop tunnel run? Survives a rebuild of the branches.</summary>
    private bool _desktopWanted;

    private long _sequence;
    private bool _disposed;

    // ================= Devices that come and go =================

    /// <summary>How long to wait after the last device notification before rebuilding.</summary>
    /// <remarks>
    /// A single plug-in produces several notifications (device added, state active, new default). One rebuild
    /// per notification would mean three interruptions in voice chat instead of one.
    /// </remarks>
    private static readonly TimeSpan RecoveryDelay = TimeSpan.FromMilliseconds(800);

    private readonly object _changeGate = new();
    private readonly HashSet<string> _changedDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _renderDefaultChanged;
    private bool _captureDefaultChanged;
    private bool _routeFailed;
    private Timer? _recoveryTimer;
    private DeviceWatcher? _watcher;

    /// <summary>
    /// Fires after every device notification on a background thread, even when no rebuild was needed. For
    /// views showing device lists.
    /// </summary>
    public event Action? DevicesChanged;

    public AudioEngine()
    {
        try { MediaFoundationApi.Startup(); }
        catch (Exception) { /* without MF only m4a/wma are missing; wav/mp3/ogg still work */ }
    }

    /// <summary>
    /// A branch ended by itself: device gone, driver reloaded. Reported by <c>PlaybackStopped</c> and
    /// <c>RecordingStopped</c>, and only with an error; an intended stop during rebuild comes without one.
    /// </summary>
    private void OnRouteFailed()
    {
        lock (_changeGate) _routeFailed = true;
        ScheduleRecovery();
    }

    private void OnDeviceChange(DeviceChange change)
    {
        lock (_changeGate)
        {
            if (change.DefaultChanged)
            {
                if (change.Flow == DataFlow.Render) _renderDefaultChanged = true;
                else if (change.Flow == DataFlow.Capture) _captureDefaultChanged = true;
            }
            else if (change.DeviceId is { Length: > 0 } id)
            {
                _changedDevices.Add(id);
            }
        }

        ScheduleRecovery();
    }

    private void ScheduleRecovery()
    {
        lock (_changeGate)
        {
            if (_disposed) return;
            _recoveryTimer ??= new Timer(_ => Recover(), null, Timeout.Infinite, Timeout.Infinite);
            _recoveryTimer.Change(RecoveryDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Rebuilds if the collected notifications affect a running branch.</summary>
    /// <remarks>
    /// Not on every notification: a rebuild stops playing sounds and interrupts the microphone briefly. A
    /// neighbor's Bluetooth device connecting is no reason.
    /// </remarks>
    private void Recover()
    {
        HashSet<string> devices;
        bool renderDefault, captureDefault, failed;

        lock (_changeGate)
        {
            devices = [.. _changedDevices];
            _changedDevices.Clear();
            renderDefault = _renderDefaultChanged;
            captureDefault = _captureDefaultChanged;
            failed = _routeFailed;
            _renderDefaultChanged = _captureDefaultChanged = _routeFailed = false;
        }

        lock (_gate)
        {
            if (_disposed) return;

            if (failed || Affected(devices, renderDefault, captureDefault)) Rebuild();
        }

        try { DevicesChanged?.Invoke(); } catch (Exception) { }
    }

    /// <summary>Does any notification affect a branch that runs or should run?</summary>
    private bool Affected(HashSet<string> devices, bool renderDefault, bool captureDefault)
    {
        // A wanted branch that is missing: any device notification may bring it back.
        if (_settings.VirtualOutputEnabled && _virtualRoute is null) return true;
        if (_settings.MonitorEnabled && _monitorRoute is null && devices.Count > 0) return true;
        if (_settings.MicPassthroughEnabled && _mic is null && devices.Count > 0) return true;

        // A device a branch runs on, or should run on and only fell back to the default for lack
        // of the device.
        string?[] watched =
        [
            _virtualRoute?.DeviceId, _monitorRoute?.DeviceId, _mic?.DeviceId,
            _settings.VirtualOutputDeviceId, _settings.MonitorOutputDeviceId, _settings.MicDeviceId,
        ];

        if (watched.Any(id => id is { Length: > 0 } && devices.Contains(id))) return true;

        // A new default device only affects branches that follow it.
        if (renderDefault
            && (FollowsDefault(_settings.VirtualOutputDeviceId, _virtualRoute?.DeviceId)
                || FollowsDefault(_settings.MonitorOutputDeviceId, _monitorRoute?.DeviceId)))
        {
            return true;
        }

        return captureDefault && _settings.MicPassthroughEnabled
            && FollowsDefault(_settings.MicDeviceId, _mic?.DeviceId);
    }

    /// <summary>
    /// Does the branch follow the default device, because none is chosen or because the chosen one is
    /// missing and <see cref="AudioDevices.Resolve"/> fell back?
    /// </summary>
    private static bool FollowsDefault(string? configured, string? actual) =>
        string.IsNullOrWhiteSpace(configured)
        || configured == AudioDevice.DefaultId
        || !string.Equals(configured, actual, StringComparison.OrdinalIgnoreCase);

    /// <summary>On first build, on the background thread; COM costs a few milliseconds here.</summary>
    private void EnsureWatcher()
    {
        if (_watcher is not null) return;

        try
        {
            _watcher = new DeviceWatcher(OnDeviceChange);
        }
        catch (Exception)
        {
            // Without notifications the old behavior stays: rebuild on the next device change in settings.
        }
    }

    public SampleCache Cache { get; } = new();

    /// <summary>Fires on the audio or UI thread; receivers must marshal.</summary>
    public event Action<Guid, bool>? SoundStateChanged;

    public event Action? RoutesChanged;

    /// <summary>Fires when the desktop tunnel turns on or off, including by itself.</summary>
    public event Action? DesktopTunnelChanged;

    public bool VirtualRouteActive => _virtualRoute is not null;
    public bool MonitorRouteActive => _monitorRoute is not null;
    public bool MicActive => _mic is not null;

    /// <summary>Is the microphone also on the headphones?</summary>
    public bool MicMonitorActive => _mic?.IsMonitored ?? false;

    /// <summary>
    /// Does the microphone actually reach the virtual output? False while no virtual cable is there; then
    /// pass-through is deliberately skipped.
    /// </summary>
    public bool MicReachesVirtualOutput => _mic is not null && _micInVirtualRoute;

    public string? VirtualRouteDeviceName => _virtualRoute?.DeviceName;
    public string? MonitorRouteDeviceName => _monitorRoute?.DeviceName;
    public string? MicDeviceName => _mic?.DeviceName;

    /// <summary>Last error opening a device, for the status display.</summary>
    public string? LastError { get; private set; }

    // ================= Desktop audio =================

    /// <summary>Is the tunnel running?</summary>
    public bool DesktopTunnelActive => _desktop is not null;

    /// <summary>What is being captured, or why not.</summary>
    public string? DesktopTunnelNote { get; private set; }

    /// <summary>
    /// Routes desktop audio into the virtual cable. Deliberately not saved in <see cref="AppSettings"/>: the
    /// tunnel is temporary, and a whole-system capture silently running from startup would be an unpleasant
    /// surprise.
    /// </summary>
    public bool SetDesktopTunnel(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return false;

            _desktopWanted = enabled;

            if (!enabled)
            {
                CloseDesktopTunnel();
            }
            else if (_desktop is null)
            {
                OpenDesktopTunnel();
            }
        }

        DesktopTunnelChanged?.Invoke();
        return DesktopTunnelActive;
    }

    public bool ToggleDesktopTunnel() => SetDesktopTunnel(!DesktopTunnelActive);

    /// <summary>
    /// Desktop audio goes only into the virtual cable, never to the headphones: it already plays there, that's
    /// where it comes from. Hearing it again would mean everything twice, slightly offset.
    /// </summary>
    private void OpenDesktopTunnel()
    {
        CloseDesktopTunnel();

        if (_virtualRoute is null)
        {
            DesktopTunnelNote = "No virtual output active. Desktop audio reaches nobody.";
            return;
        }

        if (!VirtualCableDetector.IsVirtualCableDevice(_virtualRoute.DeviceName))
        {
            DesktopTunnelNote = $"\"{_virtualRoute.DeviceName}\" is not a virtual cable. Desktop audio " +
                                "only reaches your own speakers.";
            return;
        }

        // The process name is stored; the process id is resolved only now. Yesterday's id points
        // to some other program today.
        DesktopCaptureTarget target;
        var appKey = _settings.DesktopTunnelApp;

        if (string.IsNullOrWhiteSpace(appKey))
        {
            target = DesktopCaptureTarget.WholeDesktop;
        }
        else
        {
            var app = RunningApps.List().FirstOrDefault(
                a => string.Equals(a.Key, appKey, StringComparison.OrdinalIgnoreCase));

            if (app is null)
            {
                DesktopTunnelNote = $"\"{appKey}\" is not running. Start the app and try again.";
                return;
            }

            target = new DesktopCaptureTarget(app.ProcessId, app.Name);
        }

        try
        {
            var capture = DesktopCapture.Open(target, _settings.MonitorOutputDeviceId, out var note);

            var backlogMs = MaxMicBacklogMs(_settings.BufferMilliseconds);
            var buffer = new BufferedWaveProvider(capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true,
                BufferDuration = TimeSpan.FromMilliseconds(Math.Max(250, backlogMs * 4)),
            };

            var chain = AudioFormat.Normalize(buffer.ToSampleProvider());
            var volume = new VolumeSampleProvider(chain)
            {
                Volume = VolumeCurve.ToAmplitude(_settings.DesktopVolume),
            };

            var tap = new CaptureTap(buffer, volume, _virtualRoute.RouteMixer, backlogMs);

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                tap.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                tap.TrimBacklog();
            };

            _virtualRoute.RouteMixer.AddMixerInput(volume);
            _desktop = new DesktopTunnel(capture, tap);

            DesktopTunnelNote = note ?? capture.Kind switch
            {
                DesktopCaptureKind.SingleApp =>
                    $"Only audio from \"{capture.SourceName}\" goes into the cable.",
                DesktopCaptureKind.ExcludingSelf =>
                    "All system audio goes into the cable, except WinTo.",
                _ => $"\"{capture.SourceName}\" is captured completely.",
            };
        }
        catch (Exception ex)
        {
            DesktopTunnelNote = $"Desktop audio: {ex.Message}";
            CloseDesktopTunnel();
        }
    }

    private void CloseDesktopTunnel()
    {
        _desktop?.Dispose();
        _desktop = null;
    }

    /// <summary>The internal processing format.</summary>
    public static string InternalFormatText =>
        $"{AudioFormat.SampleRate} Hz · stereo · 32-bit float";

    /// <summary>Format of the virtual output; if it matches, nothing is converted.</summary>
    public string? VirtualRouteFormatText => _virtualRoute?.FormatText;

    /// <summary>Has the clipping protection engaged since the last query?</summary>
    public bool ConsumeLimiterEngaged()
    {
        var virtualEngaged = _virtualRoute?.Limiter.ConsumeEngaged() ?? false;
        var monitorEngaged = _monitorRoute?.Limiter.ConsumeEngaged() ?? false;
        return virtualEngaged || monitorEngaged;
    }

    // ================= Configuration =================

    /// <summary>Applies settings. Level changes run without rebuild, device changes rebuild the affected branches.</summary>
    public void Apply(AppSettings settings)
    {
        var tunnelReopened = false;

        lock (_gate)
        {
            if (_disposed) return;

            EnsureWatcher();

            var previous = _settings;
            _settings = settings.Clone();

            // Another target app means a new capture. That only affects the tunnel, not the rest of
            // the graph; a rebuild would be too coarse.
            if (previous.DesktopTunnelApp != _settings.DesktopTunnelApp && _desktopWanted)
            {
                OpenDesktopTunnel();
                tunnelReopened = true;
            }

            Cache.BudgetBytes = Math.Max(16, _settings.CacheBudgetMegabytes) * 1024L * 1024L;
            Cache.MaxCacheSeconds = Math.Max(5, _settings.MaxCacheSeconds);

            var structural =
                previous.VirtualOutputDeviceId != _settings.VirtualOutputDeviceId ||
                previous.MonitorOutputDeviceId != _settings.MonitorOutputDeviceId ||
                previous.MicDeviceId != _settings.MicDeviceId ||
                previous.VirtualOutputEnabled != _settings.VirtualOutputEnabled ||
                previous.MonitorEnabled != _settings.MonitorEnabled ||
                previous.MicPassthroughEnabled != _settings.MicPassthroughEnabled ||
                previous.MicMonitorEnabled != _settings.MicMonitorEnabled ||
                previous.BufferMilliseconds != _settings.BufferMilliseconds ||
                (_virtualRoute is null && _monitorRoute is null);

            if (structural)
                Rebuild();
            else
                ApplyVolumes();
        }

        if (tunnelReopened) DesktopTunnelChanged?.Invoke();
    }

    /// <summary>
    /// Sound and mic levels are fully independent: the sound level acts on the submixer, the mic level on its
    /// own tap.
    ///
    /// All slider positions go through <see cref="VolumeCurve"/>: the position is stored and shown, the gain
    /// factor goes into the graph.
    /// </summary>
    private void ApplyVolumes()
    {
        if (_virtualRoute is not null)
            _virtualRoute.SoundVolume.Volume = VirtualSoundAmplitude();

        if (_monitorRoute is not null)
            _monitorRoute.SoundVolume.Volume = MonitorSoundAmplitude();

        _mic?.SetVolume(VolumeCurve.ToAmplitude(_settings.MicVolume));
        _desktop?.SetVolume(VolumeCurve.ToAmplitude(_settings.DesktopVolume));

        // The protection can be toggled live, no rebuild needed.
        if (_virtualRoute is not null) _virtualRoute.Limiter.Enabled = _settings.LimiterEnabled;
        if (_monitorRoute is not null) _monitorRoute.Limiter.Enabled = _settings.LimiterEnabled;
    }

    /// <summary>Reads the device's mix format for display.</summary>
    private static string DescribeFormat(MMDevice device)
    {
        try
        {
            var mix = device.AudioClient.MixFormat;
            var channels = mix.Channels == 1 ? "mono" : mix.Channels == 2 ? "stereo" : $"{mix.Channels} channels";
            return $"{mix.SampleRate} Hz · {channels} · {mix.BitsPerSample}-bit";
        }
        catch (Exception)
        {
            return "Format unknown";
        }
    }

    private float VirtualSoundAmplitude() => VolumeCurve.ToAmplitude(_settings.MasterVolume);

    /// <summary>Two sliders in series: multiplying factors means adding decibels.</summary>
    private float MonitorSoundAmplitude() =>
        VolumeCurve.ToAmplitude(_settings.MasterVolume) * VolumeCurve.ToAmplitude(_settings.MonitorVolume);

    // ================= Latency budget =================

    /// <summary>Capture buffer. NAudio's default of 100 ms is far too sluggish for speech.</summary>
    public static int CaptureBufferMs(int bufferMs) => Math.Clamp(bufferMs / 2, 10, 40);

    /// <summary>
    /// Allowed backlog in the intermediate buffer before trimming. Without this limit clock drift between
    /// microphone and output grows it forever; the delay builds up and never goes away.
    /// </summary>
    public static int MaxMicBacklogMs(int bufferMs) => Math.Clamp(bufferMs, 20, 60);

    public static int EstimateMicLatencyMs(int bufferMs) =>
        CaptureBufferMs(bufferMs) + MaxMicBacklogMs(bufferMs) + bufferMs;

    private void Rebuild()
    {
        StopAllInternal();

        // The tunnel hangs on the virtual branch, which is about to go away.
        CloseDesktopTunnel();

        _mic?.Dispose();
        _mic = null;
        _virtualRoute?.Dispose();
        _virtualRoute = null;
        _monitorRoute?.Dispose();
        _monitorRoute = null;

        // No audio thread reads anymore; whatever still ran is ended here, or it would stay in
        // _active forever.
        FinishActiveVoices();

        LastError = null;

        if (_settings.VirtualOutputEnabled)
        {
            _virtualRoute = OpenRoute(_settings.VirtualOutputDeviceId, VirtualSoundAmplitude(), "Virtual cable");
        }

        if (_settings.MonitorEnabled && !MonitorWouldDuplicate())
        {
            _monitorRoute = OpenRoute(
                _settings.MonitorOutputDeviceId,
                MonitorSoundAmplitude(),
                "Monitor");
        }

        _micInVirtualRoute = false;

        if (_settings.MicPassthroughEnabled)
        {
            var targets = new List<MixingSampleProvider>(2);

            // The microphone belongs only in a virtual cable. If the virtual output points to a real
            // playback device (the normal case until a cable is set up), pass-through would put your
            // own voice on your own headphones, and with speakers create a feedback loop.
            if (_virtualRoute is not null)
            {
                if (VirtualCableDetector.IsVirtualCableDevice(_virtualRoute.DeviceName))
                {
                    targets.Add(_virtualRoute.RouteMixer);
                    _micInVirtualRoute = true;
                }
                else
                {
                    LastError = $"Microphone not passed through: \"{_virtualRoute.DeviceName}\" " +
                                "is not a virtual cable.";
                }
            }

            // Monitoring yourself is a deliberate user choice and therefore exempt from the cable check.
            if (_settings.MicMonitorEnabled && _monitorRoute is not null)
                targets.Add(_monitorRoute.RouteMixer);

            if (targets.Count > 0)
                _mic = OpenMic(_settings.MicDeviceId, targets, VolumeCurve.ToAmplitude(_settings.MicVolume));
        }

        // If the tunnel was on, it attaches to the new branch.
        if (_desktopWanted) OpenDesktopTunnel();

        RoutesChanged?.Invoke();
        DesktopTunnelChanged?.Invoke();
    }

    /// <summary>
    /// If virtual output and monitor point to the same device, every sound would arrive there twice: twice as
    /// loud and comb-filtered by the slightly offset clocks. The normal case until a cable is set up.
    /// </summary>
    private bool MonitorWouldDuplicate()
    {
        if (_virtualRoute is null) return false;

        using var monitor = AudioDevices.Resolve(_settings.MonitorOutputDeviceId, capture: false);
        if (monitor is null) return false;

        if (monitor.ID != _virtualRoute.DeviceId) return false;

        LastError = "Monitor skipped: same device as the virtual output.";
        return true;
    }

    private OutputRoute? OpenRoute(string? deviceId, float volume, string label)
    {
        MMDevice? device = null;
        try
        {
            device = AudioDevices.Resolve(deviceId, capture: false);
            if (device is null)
            {
                LastError = $"{label}: no playback device found.";
                return null;
            }

            // Stage 1: only the sounds, with their own level.
            var soundMixer = new MixingSampleProvider(AudioFormat.Engine) { ReadFully = true };
            var soundVolume = new VolumeSampleProvider(soundMixer) { Volume = volume };

            // Stage 2: sounds + microphone. What arrives here is already leveled.
            var routeMixer = new MixingSampleProvider(AudioFormat.Engine) { ReadFully = true };
            routeMixer.AddMixerInput(soundVolume);

            // Stage 3: last stop before the device. Only here can the sum of sounds and microphone
            // leave the valid range.
            var limiter = new SoftLimiterSampleProvider(routeMixer) { Enabled = _settings.LimiterEnabled };

            var output = new WasapiOut(
                device,
                AudioClientShareMode.Shared,
                useEventSync: true,
                latency: Math.Clamp(_settings.BufferMilliseconds, 10, 400));

            output.Init(limiter);

            // With an error means the device is gone or the driver reloaded. Without one, the event
            // comes from an intended stop during rebuild.
            output.PlaybackStopped += (_, e) =>
            {
                if (e.Exception is not null) OnRouteFailed();
            };

            output.Play();

            var route = new OutputRoute(
                device, output, soundMixer, soundVolume, routeMixer, limiter,
                device.FriendlyName, device.ID, DescribeFormat(device));
            device = null; // ownership transferred
            return route;
        }
        catch (Exception ex)
        {
            LastError = $"{label}: {ex.Message}";
            device?.Dispose();
            return null;
        }
    }

    private MicPassthrough? OpenMic(string? deviceId, IReadOnlyList<MixingSampleProvider> targets, float volume)
    {
        MMDevice? device = null;
        WasapiCapture? capture = null;
        try
        {
            device = AudioDevices.Resolve(deviceId, capture: true);
            if (device is null)
            {
                LastError = "Microphone: no recording device found.";
                return null;
            }

            // Don't record the cable itself; that would create a feedback loop.
            if (VirtualCableDetector.IsVirtualCableDevice(device.FriendlyName))
            {
                LastError = "Microphone skipped: virtual cable selected as input (feedback).";
                device.Dispose();
                return null;
            }

            // NAudio's default would be a 100 ms capture buffer, unacceptable for speech.
            var captureMs = CaptureBufferMs(_settings.BufferMilliseconds);
            capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: captureMs)
            {
                ShareMode = AudioClientShareMode.Shared,
            };

            // One tap per target. Two mixers must not share a buffer: each Read consumes the samples,
            // so they would take the signal from each other and both hear only fragments.
            var backlogMs = MaxMicBacklogMs(_settings.BufferMilliseconds);
            var taps = new List<CaptureTap>(targets.Count);

            foreach (var target in targets)
            {
                var buffer = new BufferedWaveProvider(capture.WaveFormat)
                {
                    DiscardOnBufferOverflow = true,
                    ReadFully = true,
                    BufferDuration = TimeSpan.FromMilliseconds(Math.Max(250, backlogMs * 4)),
                };

                var chain = AudioFormat.Normalize(buffer.ToSampleProvider());
                var volumeProvider = new VolumeSampleProvider(chain) { Volume = volume };

                target.AddMixerInput(volumeProvider);
                taps.Add(new CaptureTap(buffer, volumeProvider, target, backlogMs));
            }

            capture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;

                foreach (var tap in taps)
                {
                    tap.Buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    tap.TrimBacklog();
                }
            };

            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null) OnRouteFailed();
            };

            capture.StartRecording();

            var mic = new MicPassthrough(device, capture, taps, device.FriendlyName, device.ID);
            device = null;
            capture = null;
            return mic;
        }
        catch (Exception ex)
        {
            LastError = $"Microphone: {ex.Message}";
            capture?.Dispose();
            device?.Dispose();
            return null;
        }
    }

    // ================= Playback =================

    public bool IsPlaying(Guid soundId)
    {
        lock (_activeGate) return _active.ContainsKey(soundId);
    }

    /// <summary>
    /// State of the most recently started playback for display. Polled by the UI instead of raised as an event:
    /// the position changes continuously on the audio thread, and an event per sample would be absurd.
    /// </summary>
    public PlaybackStatus? GetStatus()
    {
        lock (_activeGate)
        {
            if (_active.Count == 0) return null;

            Playback? newest = null;
            foreach (var playback in _active.Values)
            {
                if (newest is null || playback.Sequence > newest.Sequence) newest = playback;
            }

            return newest?.ToStatus(_active.Count);
        }
    }

    public void SetPaused(Guid soundId, bool paused)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        playback?.SetPaused(paused);
    }

    /// <summary>Toggles pause and returns the new state.</summary>
    public bool TogglePause(Guid soundId)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        if (playback is null) return false;

        var paused = !playback.IsPaused;
        playback.SetPaused(paused);
        return paused;
    }

    /// <summary>Starts the sound, or stops it if already playing. Returns the new state.</summary>
    public bool Toggle(SoundEntry entry)
    {
        if (IsPlaying(entry.Id))
        {
            Stop(entry.Id);
            return false;
        }

        return Play(entry);
    }

    public bool Play(SoundEntry entry)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_virtualRoute is null && _monitorRoute is null) return false;

            if (_settings.StopOthersOnPlay) StopAllInternal();
            else Stop(entry.Id);

            var cached = Cache.GetOrLoad(entry.FilePath);
            if (cached is null && !File.Exists(entry.FilePath)) return false;

            var playback = new Playback(entry.Id, entry.Name, entry.Loop, Interlocked.Increment(ref _sequence));
            var volume = VolumeCurve.ToAmplitude(Math.Clamp(entry.Volume, 0f, 2f));

            if (_virtualRoute is not null) AttachVoice(playback, _virtualRoute, entry, cached, volume);
            if (_monitorRoute is not null) AttachVoice(playback, _monitorRoute, entry, cached, volume);

            if (playback.VoiceCount == 0) return false;

            lock (_activeGate) _active[entry.Id] = playback;

            // Only clean up if this is still the current playback. Otherwise a quickly retriggered
            // sound risks the fading previous instance removing its entry.
            playback.Completed += id =>
            {
                bool wasCurrent;
                lock (_activeGate)
                {
                    wasCurrent = _active.TryGetValue(id, out var current) && ReferenceEquals(current, playback);
                    if (wasCurrent) _active.Remove(id);
                }

                if (wasCurrent) SoundStateChanged?.Invoke(id, false);
            };

            playback.Arm();

            SoundStateChanged?.Invoke(entry.Id, true);
            return true;
        }
    }

    private void AttachVoice(Playback playback, OutputRoute route, SoundEntry entry, CachedSound? cached, float volume)
    {
        try
        {
            ISampleProvider source;
            IDisposable? owner = null;

            if (cached is not null)
            {
                source = new CachedSoundSource(cached, entry.Loop);
            }
            else
            {
                var streaming = new StreamingSource(entry.FilePath, entry.Loop);
                source = streaming;
                owner = streaming;
            }

            var voice = new Voice(source, owner, volume);
            playback.Add(voice);
            route.SoundMixer.AddMixerInput(voice);
        }
        catch (Exception ex)
        {
            LastError = $"Playback: {ex.Message}";
        }
    }

    public void Stop(Guid soundId)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        playback?.Stop();
    }

    public void StopAll()
    {
        lock (_gate) StopAllInternal();
    }

    private void StopAllInternal()
    {
        Playback[] all;
        lock (_activeGate) all = _active.Values.ToArray();
        foreach (var playback in all) playback.Stop();
    }

    /// <summary>Ends everything still in <see cref="_active"/>, immediately, without fade.</summary>
    /// <remarks>
    /// Belongs <b>after</b> disposing the output branches: <see cref="Voice.Finish"/> otherwise only runs from the
    /// audio thread, which no longer exists after Dispose. A stop via <see cref="StopAllInternal"/> alone only
    /// requests the fade; nothing reads afterwards, so the sound would stay "active" forever and a streamed one
    /// would keep its file handle. Via Completed → SoundStateChanged <see cref="_active"/> cleans itself up and the
    /// UI learns of the end.
    /// </remarks>
    private void FinishActiveVoices()
    {
        Playback[] all;
        lock (_activeGate) all = _active.Values.ToArray();
        foreach (var playback in all) playback.FinishVoices();
    }

    /// <param name="position">Slider position, not gain factor.</param>
    public void SetSoundVolume(Guid soundId, float position)
    {
        Playback? playback;
        lock (_activeGate) _active.TryGetValue(soundId, out playback);
        playback?.SetVolume(VolumeCurve.ToAmplitude(Math.Clamp(position, 0f, 2f)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            StopAllInternal();
            CloseDesktopTunnel();
            _mic?.Dispose();
            _virtualRoute?.Dispose();
            _monitorRoute?.Dispose();
            _mic = null;
            _virtualRoute = null;
            _monitorRoute = null;

            // As in Rebuild: open file handles of streamed sounds must be returned.
            FinishActiveVoices();

            Cache.Clear();
        }

        // Outside the lock: a running audio service callback would otherwise wait on _gate while
        // unregistering waits on it.
        lock (_changeGate)
        {
            _recoveryTimer?.Dispose();
            _recoveryTimer = null;
        }

        _watcher?.Dispose();
        _watcher = null;

        try { MediaFoundationApi.Shutdown(); }
        catch (Exception) { /* irrelevant during shutdown */ }
    }

    // ================= internal types =================

    private sealed class OutputRoute(
        MMDevice device,
        WasapiOut output,
        MixingSampleProvider soundMixer,
        VolumeSampleProvider soundVolume,
        MixingSampleProvider routeMixer,
        SoftLimiterSampleProvider limiter,
        string deviceName,
        string deviceId,
        string formatText) : IDisposable
    {
        public SoftLimiterSampleProvider Limiter { get; } = limiter;

        /// <summary>The device's actual format, to compare with the internal one.</summary>
        public string FormatText { get; } = formatText;

        /// <summary>Sounds go in here.</summary>
        public MixingSampleProvider SoundMixer { get; } = soundMixer;

        /// <summary>Sound level; doesn't affect the microphone.</summary>
        public VolumeSampleProvider SoundVolume { get; } = soundVolume;

        /// <summary>The microphone goes in here, after the sound level.</summary>
        public MixingSampleProvider RouteMixer { get; } = routeMixer;

        public string DeviceName { get; } = deviceName;
        public string DeviceId { get; } = deviceId;

        public void Dispose()
        {
            try { output.Stop(); } catch (Exception) { /* device may already be gone */ }
            try { output.Dispose(); } catch (Exception) { }
            try { device.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>A tap of a capture signal for exactly one mixer.</summary>
    private sealed class CaptureTap(
        BufferedWaveProvider buffer,
        VolumeSampleProvider volume,
        MixingSampleProvider target,
        int maxBacklogMs)
    {
        private readonly int _maxBacklogBytes = buffer.WaveFormat.AverageBytesPerSecond * maxBacklogMs / 1000;
        private byte[] _scratch = [];

        public BufferedWaveProvider Buffer { get; } = buffer;
        public VolumeSampleProvider Volume { get; } = volume;
        public MixingSampleProvider Target { get; } = target;

        /// <summary>
        /// Keeps the backlog small. Microphone and output run on separate clocks; if the mic records even a
        /// fraction faster than the output consumes, the buffer fills inexorably and the delay builds over minutes.
        ///
        /// Only the excess is dropped, not the whole buffer, so the voice stays continuous and at most a few
        /// milliseconds are missing.
        /// </summary>
        public void TrimBacklog()
        {
            var excess = Buffer.BufferedBytes - _maxBacklogBytes;
            if (excess <= 0) return;

            excess -= excess % Buffer.WaveFormat.BlockAlign;
            if (excess <= 0) return;

            if (_scratch.Length < excess) _scratch = new byte[excess];
            Buffer.Read(_scratch, 0, excess);
        }
    }

    /// <summary>Desktop audio on its way into the virtual cable.</summary>
    private sealed class DesktopTunnel(DesktopCapture capture, CaptureTap tap) : IDisposable
    {
        public void SetVolume(float volume) => tap.Volume.Volume = volume;

        public void Dispose()
        {
            // Silence the source first, then remove the tap from the mixer; otherwise the capture
            // thread would still write into a dead buffer.
            try { capture.Dispose(); } catch (Exception) { }
            try { tap.Target.RemoveMixerInput(tap.Volume); } catch (Exception) { }
            tap.Buffer.ClearBuffer();
        }
    }

    private sealed class MicPassthrough(
        MMDevice device,
        WasapiCapture capture,
        IReadOnlyList<CaptureTap> taps,
        string deviceName,
        string deviceId) : IDisposable
    {
        public string DeviceName { get; } = deviceName;

        public string DeviceId { get; } = deviceId;

        /// <summary>Is the microphone also on the headphones?</summary>
        public bool IsMonitored => taps.Count > 1;

        public void SetVolume(float volume)
        {
            foreach (var tap in taps) tap.Volume.Volume = volume;
        }

        public void Dispose()
        {
            try { capture.StopRecording(); } catch (Exception) { }
            try { capture.Dispose(); } catch (Exception) { }

            foreach (var tap in taps)
            {
                try { tap.Target.RemoveMixerInput(tap.Volume); } catch (Exception) { }
                tap.Buffer.ClearBuffer();
            }

            try { device.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>A sound playing in several branches at once.</summary>
    private sealed class Playback(Guid soundId, string name, bool loop, long sequence)
    {
        private readonly List<Voice> _voices = [];
        private bool _paused;

        public long Sequence { get; } = sequence;

        public bool IsPaused => _paused;

        public void SetPaused(bool paused)
        {
            _paused = paused;
            foreach (var voice in _voices) voice.SetPaused(paused);
        }

        /// <summary>The first voice leads: all branches play the same source in sync, one is enough for position and length.</summary>
        public PlaybackStatus ToStatus(int activeCount)
        {
            var lead = _voices.Count > 0 ? _voices[0] : null;

            return new PlaybackStatus(
                soundId,
                name,
                lead?.Position ?? TimeSpan.Zero,
                lead?.Duration ?? TimeSpan.Zero,
                _paused,
                loop,
                activeCount);
        }


        // Starts at 1: this "construction token" keeps the counter up while voices are still
        // being added. Otherwise a very short file could finish in the first branch before the
        // second was even attached.
        private int _remaining = 1;
        private int _completedFired;

        public int VoiceCount => _voices.Count;

        public event Action<Guid>? Completed;

        public void Add(Voice voice)
        {
            _voices.Add(voice);
            Interlocked.Increment(ref _remaining);
            voice.Finished += (_, _) => Decrement();
        }

        /// <summary>Releases the construction token.</summary>
        public void Arm() => Decrement();

        public void Stop()
        {
            foreach (var voice in _voices) voice.Stop();
        }

        /// <summary>Ends all voices immediately; see <see cref="Voice.FinishNow"/>.</summary>
        public void FinishVoices()
        {
            foreach (var voice in _voices) voice.FinishNow();
        }

        public void SetVolume(float volume)
        {
            foreach (var voice in _voices) voice.Volume = volume;
        }

        private void Decrement()
        {
            if (Interlocked.Decrement(ref _remaining) == 0 &&
                Interlocked.Exchange(ref _completedFired, 1) == 0)
            {
                Completed?.Invoke(soundId);
            }
        }
    }
}
