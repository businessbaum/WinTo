using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Modules.Soundboard.Audio;
using MultiTool.Modules.Soundboard.Models;
using MultiTool.Modules.Soundboard.Services;

namespace MultiTool.Modules.Soundboard.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const string CableDownloadUrl = "https://vb-audio.com/Cable/";

    private readonly AppServices _services;
    private readonly IDialogService _dialogs;
    private readonly Action _bindingsChanged;
    private readonly Action _statusChanged;

    private bool _loading;

    [ObservableProperty] private AudioDevice? _selectedVirtualDevice;
    [ObservableProperty] private AudioDevice? _selectedMonitorDevice;
    [ObservableProperty] private AudioDevice? _selectedMicDevice;
    [ObservableProperty] private bool _isRecordingStopAll;
    [ObservableProperty] private bool _isRecordingDesktopTunnel;
    [ObservableProperty] private string? _setupMessage;
    [ObservableProperty] private bool _setupMessageIsGood;

    public SettingsViewModel(AppServices services, IDialogService dialogs, Action bindingsChanged, Action statusChanged)
    {
        _services = services;
        _dialogs = dialogs;
        _bindingsChanged = bindingsChanged;
        _statusChanged = statusChanged;

        // Deliberately NO device search here.
        //
        // AudioDevices.Render() and .Capture() each walk all endpoints and read every display
        // name from the property store, 487 ms together. In the constructor that half second
        // would sit in the tile click. The module supplies the lists once they're ready in the
        // background; until then this says it's searching.
        CableTitle = "Searching for audio devices…";
        CableStatusText = string.Empty;
    }

    private AppSettings Settings => _services.Settings;

    public ObservableCollection<AudioDevice> RenderDevices { get; } = [];

    public ObservableCollection<AudioDevice> CaptureDevices { get; } = [];

    // ================= Routing =================

    public bool VirtualOutputEnabled
    {
        get => Settings.VirtualOutputEnabled;
        set => Set(v => Settings.VirtualOutputEnabled = v, value, Settings.VirtualOutputEnabled);
    }

    public bool MonitorEnabled
    {
        get => Settings.MonitorEnabled;
        set => Set(v => Settings.MonitorEnabled = v, value, Settings.MonitorEnabled);
    }

    public bool MicPassthroughEnabled
    {
        get => Settings.MicPassthroughEnabled;
        set => Set(v => Settings.MicPassthroughEnabled = v, value, Settings.MicPassthroughEnabled);
    }

    /// <summary>Off = you don't hear yourself. That's the default.</summary>
    public bool MicMonitorEnabled
    {
        get => Settings.MicMonitorEnabled;
        set => Set(v => Settings.MicMonitorEnabled = v, value, Settings.MicMonitorEnabled, nameof(MicMonitorHint));
    }

    public string MicMonitorHint => Settings.MicMonitorEnabled
        ? "Your own voice comes back with about 60 ms delay. That makes speaking harder."
        : "The microphone goes only into the cable, not to your own headphones.";

    /// <summary>Visible when pass-through is on but does nothing for lack of a virtual cable. Without this hint the switch would lie.</summary>
    public bool ShowMicRoutingWarning =>
        Settings.MicPassthroughEnabled && !_services.Engine.MicReachesVirtualOutput;

    public string MicRoutingWarning =>
        "No effect: the virtual output points to a real playback device, " +
        "so the microphone isn't routed there (feedback). " +
        "Set up a virtual cable above.";

    /// <summary>Called after every rebuild of the audio chain.</summary>
    public void NotifyEngineStateChanged()
    {
        OnPropertyChanged(nameof(ShowMicRoutingWarning));
        OnPropertyChanged(nameof(MicRoutingWarning));
        OnPropertyChanged(nameof(FormatText));
    }

    /// <summary>About 3 s at a 120 ms tick: long enough to read, short enough to be accurate.</summary>
    private const int LimiterHoldTicks = 25;

    private int _limiterHold;

    /// <summary>Called on the display tick while the settings are visible.</summary>
    /// <remarks>
    /// The hint holds a few seconds after the last intervention, then clears. Before, it latched: one clip meant
    /// "engaged" for the rest of the session, a warning that no longer holds.
    /// </remarks>
    public void PollLimiter()
    {
        if (_services.Engine.ConsumeLimiterEngaged())
        {
            LimiterEngaged = true;
            _limiterHold = LimiterHoldTicks;
        }
        else if (LimiterEngaged && --_limiterHold <= 0)
        {
            LimiterEngaged = false;
        }
    }

    /// <summary>Nothing plays anymore; the intervention hint has nothing to report.</summary>
    public void ResetLimiter()
    {
        LimiterEngaged = false;
        _limiterHold = 0;
    }

    // ================= Levels =================

    public double MasterVolume
    {
        get => Settings.MasterVolume;
        set => Set(v => Settings.MasterVolume = (float)v, value, Settings.MasterVolume, nameof(MasterVolumeText));
    }

    public double MonitorVolume
    {
        get => Settings.MonitorVolume;
        set => Set(v => Settings.MonitorVolume = (float)v, value, Settings.MonitorVolume, nameof(MonitorVolumeText));
    }

    public double MicVolume
    {
        get => Settings.MicVolume;
        set => Set(v => Settings.MicVolume = (float)v, value, Settings.MicVolume, nameof(MicVolumeText));
    }

    /// <summary>The header sliders write the same settings; refresh here.</summary>
    public void NotifyLevelsChanged()
    {
        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(MicVolume));
        OnPropertyChanged(nameof(MicVolumeText));
    }

    public string MasterVolumeText => $"{Settings.MasterVolume * 100:0}%";
    public string MonitorVolumeText => $"{Settings.MonitorVolume * 100:0}%";
    public string MicVolumeText => $"{Settings.MicVolume * 100:0}%";

    // ================= Audio =================

    public double BufferMilliseconds
    {
        get => Settings.BufferMilliseconds;
        set
        {
            var rounded = (int)Math.Round(value);
            if (rounded == Settings.BufferMilliseconds) return;

            Settings.BufferMilliseconds = rounded;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BufferText));
            OnPropertyChanged(nameof(MicLatencyText));

            if (_loading) return;
            ApplyToEngine();
        }
    }

    public string BufferText => $"{Settings.BufferMilliseconds} ms";

    /// <summary>The actual speaking delay: capture buffer, allowed backlog and output buffer summed.</summary>
    public string MicLatencyText =>
        $"Microphone delay: about {AudioEngine.EstimateMicLatencyMs(Settings.BufferMilliseconds)} ms";

    public bool LimiterEnabled
    {
        get => Settings.LimiterEnabled;
        set => Set(v => Settings.LimiterEnabled = v, value, Settings.LimiterEnabled);
    }

    [ObservableProperty] private bool _limiterEngaged;

    /// <summary>Internal processing format next to the device's. If they match, nothing is converted or dropped on the way to the cable.</summary>
    public string FormatText
    {
        get
        {
            var device = _services.Engine.VirtualRouteFormatText;
            if (device is null) return $"Internal: {AudioEngine.InternalFormatText}";

            var internalText = AudioEngine.InternalFormatText;
            var matches = device.StartsWith($"{AudioFormat.SampleRate} Hz", StringComparison.Ordinal);

            return $"Internal: {internalText}\nCable:    {device}\n" +
                   (matches
                       ? "Identical. Nothing is converted on the way to the cable."
                       : "Different sample rate, Windows converts. Set the cable to " +
                         $"{AudioFormat.SampleRate} Hz in Windows sound settings.");
        }
    }

    public double CacheBudgetMegabytes
    {
        get => Settings.CacheBudgetMegabytes;
        set => Set(v => Settings.CacheBudgetMegabytes = (int)Math.Round(v), value, Settings.CacheBudgetMegabytes, nameof(CacheText));
    }

    public string CacheText => $"{Settings.CacheBudgetMegabytes} MB";

    public string CacheUsageText
    {
        get
        {
            var cache = _services.Engine.Cache;
            var label = cache.Count == 1 ? "1 sound" : $"{cache.Count} sounds";
            return $"{label} in RAM · {cache.UsedBytes / (1024.0 * 1024.0):0.0} MB";
        }
    }

    // ================= Hotkeys =================

    public bool HotkeysEnabled
    {
        get => Settings.HotkeysEnabled;
        set
        {
            if (value == Settings.HotkeysEnabled) return;
            Settings.HotkeysEnabled = value;
            _services.Hotkeys.Enabled = value;
            OnPropertyChanged();
            _services.SaveSettingsSoon();
        }
    }

    public bool SuppressHotkeysInOtherApps
    {
        get => Settings.SuppressHotkeysInOtherApps;
        set
        {
            if (value == Settings.SuppressHotkeysInOtherApps) return;
            Settings.SuppressHotkeysInOtherApps = value;
            _services.Hotkeys.SuppressMatched = value;
            OnPropertyChanged();
            _services.SaveSettingsSoon();
        }
    }

    public bool StopOthersOnPlay
    {
        get => Settings.StopOthersOnPlay;
        set => Set(v => Settings.StopOthersOnPlay = v, value, Settings.StopOthersOnPlay);
    }

    public string StopAllHotkeyText => IsRecordingStopAll
        ? "Press a key…"
        : Settings.StopAllHotkey is { IsEmpty: false } hotkey ? hotkey.DisplayName : "Hotkey…";

    [RelayCommand]
    private void RecordStopAllHotkey()
    {
        if (IsRecordingStopAll)
        {
            _services.Recorder.Cancel();
            return;
        }

        IsRecordingStopAll = true;

        _services.Recorder.Begin(hotkey =>
        {
            IsRecordingStopAll = false;
            if (hotkey is null) return;

            Settings.StopAllHotkey = hotkey;
            OnPropertyChanged(nameof(StopAllHotkeyText));
            _services.SaveSettingsSoon();
            _bindingsChanged();
        });
    }

    [RelayCommand]
    private void ClearStopAllHotkey()
    {
        Settings.StopAllHotkey = null;
        OnPropertyChanged(nameof(StopAllHotkeyText));
        _services.SaveSettingsSoon();
        _bindingsChanged();
    }

    partial void OnIsRecordingStopAllChanged(bool value) => OnPropertyChanged(nameof(StopAllHotkeyText));

    public string DesktopTunnelHotkeyText => IsRecordingDesktopTunnel
        ? "Press a key…"
        : Settings.DesktopTunnelHotkey is { IsEmpty: false } hotkey ? hotkey.DisplayName : "Hotkey…";

    [RelayCommand]
    private void RecordDesktopTunnelHotkey()
    {
        if (IsRecordingDesktopTunnel)
        {
            _services.Recorder.Cancel();
            return;
        }

        IsRecordingDesktopTunnel = true;

        _services.Recorder.Begin(hotkey =>
        {
            IsRecordingDesktopTunnel = false;
            if (hotkey is null) return;

            Settings.DesktopTunnelHotkey = hotkey;
            OnPropertyChanged(nameof(DesktopTunnelHotkeyText));
            _services.SaveSettingsSoon();
            _bindingsChanged();
        });
    }

    [RelayCommand]
    private void ClearDesktopTunnelHotkey()
    {
        Settings.DesktopTunnelHotkey = null;
        OnPropertyChanged(nameof(DesktopTunnelHotkeyText));
        _services.SaveSettingsSoon();
        _bindingsChanged();
    }

    partial void OnIsRecordingDesktopTunnelChanged(bool value) =>
        OnPropertyChanged(nameof(DesktopTunnelHotkeyText));

    // ================= Virtual cable =================

    public bool CableDetected { get; private set; }

    public string CableTitle { get; private set; } = "Virtual cable";

    public string CableStatusText { get; private set; } = string.Empty;

    public string CableInstruction { get; private set; } = string.Empty;

    public bool ShowCableInstruction => CableDetected;

    private VirtualCableInfo? _cable;

    [RelayCommand]
    /// <summary>Queries and shows devices. Costs about half a second.</summary>
    private void RefreshDevices() => ApplyDevices(AudioDevices.Render(), AudioDevices.Capture());

    /// <summary>Display only. The query belongs on a background thread; this half touches observable collections and must run on the UI thread.</summary>
    public void ApplyDevices(List<AudioDevice> render, List<AudioDevice> capture)
    {
        _loading = true;
        try
        {
            RenderDevices.Clear();
            RenderDevices.Add(AudioDevice.SystemDefaultRender);
            foreach (var device in render) RenderDevices.Add(device);

            CaptureDevices.Clear();
            CaptureDevices.Add(AudioDevice.SystemDefaultCapture);
            foreach (var device in capture) CaptureDevices.Add(device);

            SelectedVirtualDevice = Match(RenderDevices, Settings.VirtualOutputDeviceId);
            SelectedMonitorDevice = Match(RenderDevices, Settings.MonitorOutputDeviceId);
            SelectedMicDevice = Match(CaptureDevices, Settings.MicDeviceId);

            _cable = VirtualCableDetector.Detect(render, capture);
            UpdateCableStatus();
        }
        finally
        {
            _loading = false;
        }

        RaiseAll();
    }

    private void UpdateCableStatus()
    {
        CableDetected = _cable is not null;

        if (_cable is null)
        {
            CableTitle = "No virtual cable found";
            CableStatusText =
                "Without a virtual audio cable Windows can't pass the audio on as a microphone. " +
                "Install VB-CABLE, then \"Search again\".";
            CableInstruction = string.Empty;
        }
        else
        {
            CableTitle = $"{_cable.ProductName} detected";
            CableStatusText = $"Playing to: {_cable.RenderDevice.Name}";
            CableInstruction =
                $"In Discord, TeamSpeak or OBS set the input device to \"{_cable.CaptureDeviceName}\".";
        }

        OnPropertyChanged(nameof(CableDetected));
        OnPropertyChanged(nameof(CableTitle));
        OnPropertyChanged(nameof(CableStatusText));
        OnPropertyChanged(nameof(CableInstruction));
        OnPropertyChanged(nameof(ShowCableInstruction));
    }

    /// <summary>Sets everything in one step so audio reaches voice chat and your microphone keeps working.</summary>
    [RelayCommand]
    private void AutoSetup()
    {
        RefreshDevices();

        if (_cable is null)
        {
            SetupMessageIsGood = false;
            SetupMessage = "No virtual audio cable found. Install VB-CABLE.";
            return;
        }

        _loading = true;
        try
        {
            Settings.VirtualOutputEnabled = true;
            Settings.VirtualOutputDeviceId = _cable.RenderDevice.Id;

            // Monitor: anything but the cable itself, otherwise you hear nothing.
            if (Settings.MonitorOutputDeviceId is null ||
                RenderDevices.FirstOrDefault(d => d.Id == Settings.MonitorOutputDeviceId) is { } monitor &&
                VirtualCableDetector.IsVirtualCableDevice(monitor.Name))
            {
                Settings.MonitorOutputDeviceId = AudioDevice.DefaultId;
            }

            Settings.MonitorEnabled = true;

            // Pass the microphone through so you can still talk next to the sounds.
            var mic = CaptureDevices.FirstOrDefault(d => d.Id == Settings.MicDeviceId);
            if (mic is null || VirtualCableDetector.IsVirtualCableDevice(mic.Name))
            {
                Settings.MicDeviceId = AudioDevice.DefaultId;
            }

            Settings.MicPassthroughEnabled = true;

            SelectedVirtualDevice = Match(RenderDevices, Settings.VirtualOutputDeviceId);
            SelectedMonitorDevice = Match(RenderDevices, Settings.MonitorOutputDeviceId);
            SelectedMicDevice = Match(CaptureDevices, Settings.MicDeviceId);
        }
        finally
        {
            _loading = false;
        }

        ApplyToEngine();
        RaiseAll();

        SetupMessageIsGood = true;
        SetupMessage = $"Set up. In Discord or TeamSpeak set the input device to \"{_cable.CaptureDeviceName}\".";
    }

    [RelayCommand]
    private void OpenCableDownload() => _dialogs.OpenUrl(CableDownloadUrl);

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.RevealInExplorer(_services.Paths.DataDirectory);

    [RelayCommand]
    private void ClearCache()
    {
        _services.Engine.Cache.Clear();
        OnPropertyChanged(nameof(CacheUsageText));
    }

    // ================= Device selection =================

    partial void OnSelectedVirtualDeviceChanged(AudioDevice? value)
    {
        if (_loading || value is null) return;
        Settings.VirtualOutputDeviceId = value.Id;
        ApplyToEngine();
    }

    partial void OnSelectedMonitorDeviceChanged(AudioDevice? value)
    {
        if (_loading || value is null) return;
        Settings.MonitorOutputDeviceId = value.Id;
        ApplyToEngine();
    }

    partial void OnSelectedMicDeviceChanged(AudioDevice? value)
    {
        if (_loading || value is null) return;
        Settings.MicDeviceId = value.Id;
        ApplyToEngine();
    }

    // ================= Helpers =================

    private static AudioDevice? Match(IEnumerable<AudioDevice> devices, string? id)
    {
        var list = devices.ToList();
        return list.FirstOrDefault(d => d.Id == id) ?? list.FirstOrDefault();
    }

    private void Set<T>(Action<T> assign, T value, T current, string? alsoNotify = null,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current)) return;

        assign(value);
        OnPropertyChanged(propertyName);
        if (alsoNotify is not null) OnPropertyChanged(alsoNotify);

        if (_loading) return;
        ApplyToEngine();
    }

    private void ApplyToEngine()
    {
        _services.Engine.Apply(Settings);
        _services.SaveSettingsSoon();
        OnPropertyChanged(nameof(CacheUsageText));
        _statusChanged();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(VirtualOutputEnabled));
        OnPropertyChanged(nameof(MonitorEnabled));
        OnPropertyChanged(nameof(MicPassthroughEnabled));
        OnPropertyChanged(nameof(MicMonitorEnabled));
        OnPropertyChanged(nameof(MicMonitorHint));
        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MonitorVolume));
        OnPropertyChanged(nameof(MicVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(MonitorVolumeText));
        OnPropertyChanged(nameof(MicVolumeText));
        OnPropertyChanged(nameof(BufferMilliseconds));
        OnPropertyChanged(nameof(BufferText));
        OnPropertyChanged(nameof(MicLatencyText));
        OnPropertyChanged(nameof(CacheBudgetMegabytes));
        OnPropertyChanged(nameof(CacheText));
        OnPropertyChanged(nameof(CacheUsageText));
        OnPropertyChanged(nameof(HotkeysEnabled));
        OnPropertyChanged(nameof(SuppressHotkeysInOtherApps));
        OnPropertyChanged(nameof(StopOthersOnPlay));
        OnPropertyChanged(nameof(StopAllHotkeyText));
        OnPropertyChanged(nameof(DesktopTunnelHotkeyText));
    }
}
