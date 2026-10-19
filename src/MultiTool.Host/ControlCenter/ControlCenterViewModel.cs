using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Abstractions.Bluetooth;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.ControlCenter;

/// <summary>The control center: what a click on the tray icon shows.</summary>
/// <remarks>
/// <para>
/// <b>Lives exactly as long as its window.</b> Everything is read once on open and returned on
/// close (display handles, timers); nothing keeps watching devices in the background.
/// </para>
/// <para>
/// <b>Reads run in parallel on background threads.</b> Volume takes a millisecond, radios a few
/// dozen, DDC/CI up to 100 per display. Each section has a <c>…Ready</c> flag the view fades in on,
/// and the brightness rows are reserved from the last count, so nothing jumps while it loads.
/// </para>
/// <para>
/// A <c>_loading</c> guard leads every callback: read values are set through the same properties
/// the switches bind to and must not write back.
/// </para>
/// </remarks>
public sealed partial class ControlCenterViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly LatestWins<float> _volume = new(v => SystemAudio.SetVolume(v));
    private bool _loading = true;
    private bool _disposed;

    public ControlCenterViewModel(ShellViewModel shell)
    {
        _shell = shell;
        KeepAwake = shell.Settings.KeepAwake;
        DarkMode = SystemToggles.DarkMode;
        GameMode = PowerPlans.WindowsGameMode;

        for (var i = 0; i < Math.Clamp(shell.Settings.ControlCenterDisplays, 0, 4); i++)
            Displays.Add(DisplayItem.Placeholder(i + 1));

        _loading = false;
    }

    /// <summary>Set by the window: closes the control center, e.g. after jumping to Settings.</summary>
    public Action? RequestClose { get; set; }

    /// <summary>What plays now, with its buttons; read while the control center is open.</summary>
    public NowPlaying Media { get; } = new();

    /// <summary>Reads everything. Returns immediately; the parts arrive one by one.</summary>
    public void Start()
    {
        Media.Start();
        _ = LoadAsync(() => SystemAudio.Read(), ApplyAudio, "Audio");
        _ = LoadAsync(SystemToggles.Network, ApplyNetwork, "Network");
        _ = LoadAsync(() => (Dnd: SystemToggles.DoNotDisturb(), Battery: SystemToggles.Battery()),
            r => { ApplyDnd(r.Dnd); ApplyBattery(r.Battery); }, "System");
        _ = LoadAsync(() => (Plans: PowerPlans.All(), Active: PowerPlans.Active()), r => ApplyPlans(r.Plans, r.Active), "Power");
        _ = LoadRadiosAsync();
        _ = LoadAsync(ReadBluetooth, ApplyBluetooth, "Bluetooth");
        _ = LoadAsync(Brightness.Read, ApplyDisplays, "Brightness", () => ApplyDisplays([]));
    }

    private async Task LoadAsync<T>(Func<T> read, Action<T> apply, string what, Action? failed = null)
    {
        try
        {
            var value = await Task.Run(read);
            if (_disposed)
            {
                if (value is List<DisplayBrightness> late) Brightness.Release(late);
                return;
            }

            _loading = true;
            try
            {
                apply(value);
            }
            finally
            {
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("control-center").Warn($"{what} could not be read: {ex.Message}");
            if (!_disposed) failed?.Invoke();
        }
    }

    // ------------------------------------------------------------------ Network

    [ObservableProperty]
    public partial string NetworkText { get; set; } = "…";

    [ObservableProperty]
    public partial bool Online { get; set; } = true;

    private void ApplyNetwork(NetworkState state)
    {
        NetworkText = state.Summary;
        Online = state.Internet;
        UpdateWiFiStatus();
    }

    // ------------------------------------------------------------------ Wi-Fi

    /// <summary><c>true</c> until the radios say otherwise, so the row doesn't pop in late.</summary>
    [ObservableProperty]
    public partial bool HasWiFi { get; set; } = true;

    [ObservableProperty]
    public partial bool WiFiOn { get; set; }

    [ObservableProperty]
    public partial bool WiFiExpanded { get; set; }

    [ObservableProperty]
    public partial string WiFiStatus { get; set; } = "…";

    [ObservableProperty]
    public partial string? WiFiHint { get; set; }

    [ObservableProperty]
    public partial bool WiFiLoading { get; set; }

    [ObservableProperty]
    public partial bool HasLocationHint { get; set; }

    public ObservableCollection<WifiItem> Networks { get; } = [];

    [ObservableProperty]
    public partial bool RadiosReady { get; set; }

    [ObservableProperty]
    public partial bool RadiosBusy { get; set; }

    private async Task LoadRadiosAsync()
    {
        try
        {
            var radios = await Radios.ListAsync();
            if (_disposed) return;

            _loading = true;
            try
            {
                var wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
                var bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);

                HasWiFi = wifi is not null;
                WiFiOn = wifi?.State == RadioState.On;
                HasBluetooth = bluetooth is not null;
                BluetoothOn = bluetooth?.State == RadioState.On;
            }
            finally
            {
                _loading = false;
            }
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("control-center").Warn($"Radios could not be read: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
            {
                RadiosReady = true;
                UpdateWiFiStatus();
                UpdateBluetoothStatus();
            }
        }
    }

    partial void OnWiFiOnChanged(bool value)
    {
        UpdateWiFiStatus();
        if (_loading) return;
        _ = SwitchRadioAsync(RadioKind.WiFi, value);
    }

    partial void OnWiFiExpandedChanged(bool value)
    {
        if (value) _ = LoadNetworksAsync();
    }

    [RelayCommand]
    private void ToggleWiFiExpanded() => WiFiExpanded = !WiFiExpanded;

    private void UpdateWiFiStatus()
    {
        if (!RadiosReady) return;

        var connected = Networks.FirstOrDefault(n => n.Connected)?.Name
            ?? (NetworkText.StartsWith("Wi-Fi · ", StringComparison.Ordinal) ? NetworkText["Wi-Fi · ".Length..] : null);

        WiFiStatus = !WiFiOn ? "Off" : connected ?? "Not connected";
    }

    /// <summary>Scan once, show what is cached at once, read again when the scan is through.</summary>
    private async Task LoadNetworksAsync()
    {
        if (WiFiLoading) return;
        WiFiLoading = true;

        try
        {
            if (!WiFiOn)
            {
                Networks.Clear();
                WiFiHint = "Wi-Fi is off.";
                HasLocationHint = false;
                return;
            }

            if (Networks.Count == 0) WiFiHint = "Searching…";

            await ReadNetworksAsync(scan: true);
            await Task.Delay(2500);
            if (!_disposed && WiFiExpanded) await ReadNetworksAsync(scan: false);
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("control-center").Warn($"Wi-Fi networks could not be read: {ex}");
            if (!_disposed) WiFiHint = "Networks could not be read.";
        }
        finally
        {
            if (!_disposed) WiFiLoading = false;
        }
    }

    private async Task ReadNetworksAsync(bool scan)
    {
        var (networks, saved) = await Task.Run(() =>
        {
            if (scan) Wlan.Scan();
            return (Wlan.Networks(), Wlan.SavedProfiles());
        });
        if (_disposed) return;

        Networks.Clear();

        if (networks.Count > 0)
        {
            foreach (var network in networks.Take(8)) Networks.Add(new WifiItem(this, network));
            WiFiHint = null;
            HasLocationHint = false;
        }
        else
        {
            // Without location access Windows hides nearby networks; saved ones still connect.
            foreach (var profile in saved.Take(6))
                Networks.Add(new WifiItem(this, new WifiNetwork(profile, -1, true, false, profile)));

            WiFiHint = "Nearby networks need location access in Windows.";
            HasLocationHint = true;
        }

        UpdateWiFiStatus();
    }

    internal async Task ConnectNetworkAsync(WifiItem item)
    {
        if (item.Profile is null)
        {
            Launch("ms-availablenetworks:");
            return;
        }

        item.Busy = true;
        var problem = await Task.Run(() => item.Connected ? Wlan.Disconnect() : Wlan.Connect(item.Profile));
        if (_disposed) return;
        item.Busy = false;

        if (problem is not null) Message = problem;

        // Associating takes a few seconds.
        await Task.Delay(3000);
        if (_disposed) return;
        await ReadNetworksAsync(scan: false);
        await LoadAsync(SystemToggles.Network, ApplyNetwork, "Network");
    }

    [RelayCommand]
    private void OpenWiFiList() => Launch("ms-availablenetworks:");

    [RelayCommand]
    private void OpenLocationSettings() => Launch("ms-settings:privacy-location");

    // ------------------------------------------------------------------ Bluetooth

    [ObservableProperty]
    public partial bool HasBluetooth { get; set; } = true;

    [ObservableProperty]
    public partial bool BluetoothOn { get; set; }

    [ObservableProperty]
    public partial bool BluetoothExpanded { get; set; }

    [ObservableProperty]
    public partial string BluetoothStatus { get; set; } = "…";

    public ObservableCollection<BluetoothItem> Devices { get; } = [];

    [ObservableProperty]
    public partial bool HasDevices { get; set; }

    partial void OnBluetoothOnChanged(bool value)
    {
        UpdateBluetoothStatus();
        if (_loading) return;
        _ = SwitchRadioAsync(RadioKind.Bluetooth, value);
    }

    [RelayCommand]
    private void ToggleBluetoothExpanded() => BluetoothExpanded = !BluetoothExpanded;

    [RelayCommand]
    private void OpenBluetoothSettings() => Launch("ms-settings:bluetooth");

    /// <summary>Headsets can be connected from here; other paired devices only show their state.</summary>
    private static (List<BluetoothHeadset> Headsets, List<PairedDevice> Paired) ReadBluetooth() =>
        (BluetoothHeadsets.Snapshot(), BluetoothList.Paired());

    private void ApplyBluetooth((List<BluetoothHeadset> Headsets, List<PairedDevice> Paired) read)
    {
        Devices.Clear();

        foreach (var headset in read.Headsets) Devices.Add(new BluetoothItem(this, headset));

        var names = new HashSet<string>(read.Headsets.Select(h => h.Name), StringComparer.CurrentCulture);
        foreach (var device in read.Paired.Where(d => !names.Contains(d.Name)).OrderByDescending(d => d.Connected))
            Devices.Add(new BluetoothItem(this, device));

        HasDevices = Devices.Count > 0;
        UpdateBluetoothStatus();
    }

    private void UpdateBluetoothStatus()
    {
        if (!RadiosReady) return;

        var connected = Devices.Count(d => d.Connected);
        BluetoothStatus = !BluetoothOn ? "Off"
            : connected == 1 ? Devices.First(d => d.Connected).Name
            : connected > 1 ? $"{connected} connected"
            : "On";
    }

    /// <summary>Connects or disconnects a headset and waits until Windows reports the new state.</summary>
    /// <remarks>
    /// The driver takes the request at once but connects within seconds. Reading back right away showed the
    /// old state, and the button looked dead; now the row says what is happening until the state changes,
    /// at most <see cref="ConnectTimeout"/>, and says so if it doesn't.
    /// </remarks>
    internal async Task ToggleDeviceAsync(BluetoothItem item)
    {
        if (item.Headset is not { } headset || item.Busy) return;

        var connect = !item.Connected;
        var log = new Services.ModuleLog("control-center");

        item.Busy = true;
        item.Detail = connect ? "Connecting…" : "Disconnecting…";

        var accepted = await Task.Run(() => BluetoothHeadsets.SetConnected(headset, connect));
        log.Info($"Bluetooth {(connect ? "connect" : "disconnect")} {headset.Name}: request {(accepted ? "accepted" : "refused")} " +
                 $"({headset.EndpointIds.Count} endpoints).");
        if (_disposed) return;

        var reached = false;
        if (accepted)
        {
            for (var waited = TimeSpan.Zero; waited < ConnectTimeout && !_disposed; waited += TimeSpan.FromMilliseconds(500))
            {
                await Task.Delay(500);
                var now = await Task.Run(BluetoothHeadsets.Snapshot);
                if (now.FirstOrDefault(h => h.ContainerId == headset.ContainerId) is { } current && current.Connected == connect)
                {
                    reached = true;
                    break;
                }
            }
        }

        if (_disposed) return;

        if (!reached)
        {
            Message = !accepted
                ? $"Windows refused the request for {item.Name}. Bluetooth settings can {(connect ? "connect" : "disconnect")} it."
                : connect
                    ? $"{item.Name} did not connect within {ConnectTimeout.TotalSeconds:0} s: switched off, out of range or connected to another device."
                    : $"{item.Name} did not disconnect within {ConnectTimeout.TotalSeconds:0} s.";
            log.Warn(Message);
        }

        await LoadAsync(ReadBluetooth, ApplyBluetooth, "Bluetooth");
    }

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);

    private async Task SwitchRadioAsync(RadioKind kind, bool on)
    {
        RadiosBusy = true;
        string? problem;

        try
        {
            problem = await Radios.SetAsync(kind, on);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        if (_disposed) return;
        RadiosBusy = false;

        if (problem is null)
        {
            if (kind == RadioKind.WiFi)
            {
                // After switching, Wi-Fi disconnects or reconnects; the header and the list should follow.
                _ = RefreshNetworkLaterAsync();
                if (WiFiExpanded) _ = LoadNetworksAsync();
            }
            else
            {
                await Task.Delay(1500);
                if (!_disposed) await LoadAsync(ReadBluetooth, ApplyBluetooth, "Bluetooth");
            }
            return;
        }

        Message = problem;

        // Reset the switch: it shows what is, not what was wanted.
        _loading = true;
        if (kind == RadioKind.WiFi) WiFiOn = !on;
        else BluetoothOn = !on;
        _loading = false;
    }

    private async Task RefreshNetworkLaterAsync()
    {
        foreach (var delay in new[] { 1500, 3000 })
        {
            await Task.Delay(delay);
            if (_disposed) return;
            await LoadAsync(SystemToggles.Network, ApplyNetwork, "Network");
        }
    }

    // ------------------------------------------------------------------ Do not disturb, dark mode, keep awake

    [ObservableProperty]
    public partial bool DndOn { get; set; }

    [ObservableProperty]
    public partial string DndText { get; set; } = "…";

    private void ApplyDnd(bool? on)
    {
        DndOn = on == true;
        DndText = on switch { true => "on", false => "off", null => "unknown" };
    }

    /// <summary>Opens the notification settings, where Do Not Disturb is at the top.</summary>
    /// <remarks>Why it isn't switched here: see <see cref="SystemToggles.DoNotDisturb"/>.</remarks>
    [RelayCommand]
    private void OpenNotificationSettings() => Launch("ms-settings:notifications");

    [ObservableProperty]
    public partial bool DarkMode { get; set; }

    partial void OnDarkModeChanged(bool value)
    {
        if (_loading) return;

        if (SystemToggles.SetDarkMode(value) is { } problem)
        {
            Message = problem;
            _loading = true;
            DarkMode = !value;
            _loading = false;
        }
    }

    [ObservableProperty]
    public partial bool KeepAwake { get; set; }

    /// <summary>Windows Game Mode, the same switch as in the game mode module.</summary>
    [ObservableProperty]
    public partial bool GameMode { get; set; }

    partial void OnGameModeChanged(bool value)
    {
        if (_loading) return;

        if (PowerPlans.SetWindowsGameMode(value) is { } problem)
        {
            Message = problem;
            _loading = true;
            GameMode = !value;
            _loading = false;
        }
    }

    /// <summary>The same switch as in the header and on the settings page.</summary>
    partial void OnKeepAwakeChanged(bool value)
    {
        if (_loading) return;
        _shell.SetKeepAwake(value);
    }

    // ------------------------------------------------------------------ Sound

    [ObservableProperty]
    public partial bool AudioReady { get; set; }

    [ObservableProperty]
    public partial bool HasAudio { get; set; } = true;

    [ObservableProperty]
    public partial double Volume { get; set; }

    [ObservableProperty]
    public partial bool Muted { get; set; }

    [ObservableProperty]
    public partial bool HasMicrophone { get; set; } = true;

    [ObservableProperty]
    public partial bool MicrophoneMuted { get; set; }

    [ObservableProperty]
    public partial bool OutputsExpanded { get; set; }

    [ObservableProperty]
    public partial string OutputName { get; set; } = "…";

    public ObservableCollection<OutputItem> Outputs { get; } = [];

    public string VolumeText => Muted ? "muted" : $"{Math.Round(Volume)}%";

    private void ApplyAudio(AudioState state)
    {
        HasAudio = state.DefaultOutputId is not null;
        Volume = Math.Round(state.Volume * 100);
        Muted = state.Muted;
        HasMicrophone = state.HasMicrophone;
        MicrophoneMuted = state.MicrophoneMuted;

        // Two monitors of the same model report the same name; a number tells them apart.
        Outputs.Clear();
        foreach (var group in state.Outputs.GroupBy(o => o.Name))
        {
            var index = 1;
            foreach (var output in group)
            {
                var name = group.Count() > 1 ? $"{output.Name} ({index++})" : output.Name;
                Outputs.Add(new OutputItem(this, output, name, output.Id == state.DefaultOutputId));
            }
        }

        OutputName = Outputs.FirstOrDefault(o => o.Selected)?.Name ?? "No output device";
        AudioReady = true;
    }

    /// <summary>Every slider tick lands here; only the latest value is written.</summary>
    /// <remarks>
    /// Each set means a COM enumeration, sixty times a second while dragging.
    /// <see cref="LatestWins{T}"/> writes on a background thread and skips whatever is outdated, without
    /// holding a device while the control center is closed.
    /// </remarks>
    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (_loading || !AudioReady) return;

        _volume.Post((float)(value / 100));

        // Dragging the slider means wanting to hear something.
        if (Muted) Muted = false;
    }

    partial void OnMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (_loading) return;
        Background(() => SystemAudio.SetMuted(value));
    }

    /// <summary>The tile shows "on" while the microphone is open, like Wi-Fi and Bluetooth.</summary>
    public bool MicrophoneOn
    {
        get => !MicrophoneMuted;
        set => MicrophoneMuted = !value;
    }

    partial void OnMicrophoneMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MicrophoneOn));
        if (_loading) return;
        Background(() => SystemAudio.SetMicrophoneMuted(value));
    }

    [RelayCommand]
    private void ToggleOutputsExpanded() => OutputsExpanded = !OutputsExpanded;

    internal void SelectOutput(OutputItem item)
    {
        if (item.Selected) return;

        foreach (var output in Outputs) output.Selected = output == item;
        OutputName = item.Name;

        _ = Task.Run(async () =>
        {
            var problem = SystemAudio.SetDefaultOutput(item.Device.Id);
            var state = SystemAudio.Read();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                if (problem is not null) Message = problem;

                // The new device has its own volume; the slider should show it.
                _loading = true;
                ApplyAudio(state);
                _loading = false;
            });
        });
    }

    [RelayCommand]
    private void ToggleMute() => Muted = !Muted;

    // ------------------------------------------------------------------ Per-app volume

    [ObservableProperty]
    public partial bool AppsExpanded { get; set; }

    [ObservableProperty]
    public partial bool HasApps { get; set; }

    [ObservableProperty]
    public partial bool AppsLoading { get; set; }

    public ObservableCollection<AppVolumeItem> Apps { get; } = [];

    [RelayCommand]
    private void ToggleAppsExpanded() => AppsExpanded = !AppsExpanded;

    /// <remarks>Read on expanding, not on open: most opens are for something else.</remarks>
    partial void OnAppsExpandedChanged(bool value)
    {
        if (value) _ = LoadAppsAsync();
    }

    private async Task LoadAppsAsync()
    {
        AppsLoading = true;
        try
        {
            var apps = await Task.Run(SystemAudio.Apps);
            if (_disposed) return;

            Apps.Clear();
            foreach (var app in apps) Apps.Add(new AppVolumeItem(app));
            HasApps = Apps.Count > 0;
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("control-center").Warn($"Programs could not be read: {ex.Message}");
        }
        finally
        {
            AppsLoading = false;
        }
    }

    // ------------------------------------------------------------------ Brightness

    public ObservableCollection<DisplayItem> Displays { get; } = [];

    [ObservableProperty]
    public partial bool DisplaysReady { get; set; }

    public bool NoDisplays => DisplaysReady && Displays.Count == 0;

    partial void OnDisplaysReadyChanged(bool value) => OnPropertyChanged(nameof(NoDisplays));

    private List<DisplayBrightness> _handles = [];

    private void ApplyDisplays(List<DisplayBrightness> displays)
    {
        _handles = displays;
        Displays.Clear();
        // In the order they stand on the desk: left to right, top to bottom.
        foreach (var display in displays.OrderBy(d => d.Spot?.X ?? 0).ThenBy(d => d.Spot?.Y ?? 0))
            Displays.Add(new DisplayItem(display));
        DisplaysReady = true;
        OnPropertyChanged(nameof(NoDisplays));

        // Next time the same number of rows is reserved before reading.
        if (_shell.Settings.ControlCenterDisplays != displays.Count)
        {
            _shell.Settings.ControlCenterDisplays = displays.Count;
            _shell.SaveSettings();
        }
    }

    [RelayCommand]
    private void OpenDisplaySettings() => Launch("ms-settings:display");

    // ------------------------------------------------------------------ Power, battery, recycle bin

    public ObservableCollection<PowerPlan> Plans { get; } = [];

    [ObservableProperty]
    public partial PowerPlan? SelectedPlan { get; set; }

    /// <summary>Explicitly a <c>bool</c>: a binding on <c>Plans.Count</c> would do nothing.</summary>
    [ObservableProperty]
    public partial bool HasPlans { get; set; } = true;

    [ObservableProperty]
    public partial bool PlansReady { get; set; }

    private void ApplyPlans(IReadOnlyList<PowerPlan> plans, Guid? active)
    {
        Plans.Clear();
        foreach (var plan in plans) Plans.Add(plan);
        SelectedPlan = Plans.FirstOrDefault(p => p.Id == active);
        HasPlans = Plans.Count > 0;
        PlansReady = true;
    }

    partial void OnSelectedPlanChanged(PowerPlan? value)
    {
        if (_loading || value is null) return;
        _ = ActivatePlanAsync(value.Id);
    }

    private async Task ActivatePlanAsync(Guid id)
    {
        string? problem;
        try
        {
            problem = await Task.Run(() => PowerPlans.Activate(id));
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        if (!_disposed && problem is not null) Message = problem;
    }

    [ObservableProperty]
    public partial string? BatteryText { get; set; }

    private void ApplyBattery(BatteryState battery) =>
        BatteryText = battery.Present
            ? $"{battery.Percent}%{(battery.Charging ? " · charging" : "")}"
            : null;

    /// <summary>Windows asks for confirmation itself; the control center closes first.</summary>
    [RelayCommand]
    private void EmptyRecycleBin()
    {
        RequestClose?.Invoke();
        _ = Task.Run(() => SystemToggles.EmptyRecycleBin(0));
    }

    // ------------------------------------------------------------------ Shortcuts

    [ObservableProperty]
    public partial string? Message { get; set; }

    public bool HasMessage => Message is { Length: > 0 };

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    [RelayCommand]
    private void Lock()
    {
        RequestClose?.Invoke();
        SystemToggles.Lock();
    }

    [RelayCommand]
    private void Screenshot()
    {
        RequestClose?.Invoke();

        // "region" makes the module start the region picker right away instead of showing its view.
        // Without the module in the catalog the shell opens nothing and says so in the footer.
        _shell.OpenModule(Features.ModuleIds.Capture, argument: "region");
    }

    [RelayCommand]
    private void OpenWinTo()
    {
        RequestClose?.Invoke();
        App.ShowMainWindow();
    }

    [RelayCommand]
    private void OpenWindowsSettings() => Launch("ms-settings:");

    [RelayCommand]
    private void Quit()
    {
        RequestClose?.Invoke();
        _ = App.QuitAskingAsync();
    }

    // ------------------------------------------------------------------ Helpers

    private void Launch(string uri)
    {
        RequestClose?.Invoke();

        // Never Process.Start on the UI thread; see ExternalProcess.
        _ = Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No message for a settings page that doesn't open: the control center is already closed.
            }
        });
    }

    private static void Background(Action action) => _ = Task.Run(() =>
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // An audio device that is just disappearing is not the user's fault.
        }
    });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Media.Dispose();
        foreach (var display in Displays) display.Dispose();
        Brightness.Release(_handles);
        _handles = [];
    }
}

/// <summary>A network in the Wi-Fi list.</summary>
public sealed partial class WifiItem(ControlCenterViewModel owner, WifiNetwork network) : ObservableObject
{
    public string Name => network.Ssid;

    internal string? Profile => network.Profile;

    public bool Connected => network.Connected;

    /// <summary>Signal unknown for saved networks while Windows hides scan results.</summary>
    public bool HasSignal => network.Signal >= 0;

    public bool Bar2 => network.Signal >= 30;

    public bool Bar3 => network.Signal >= 55;

    public bool Bar4 => network.Signal >= 80;

    public string Detail => Connected ? "Connected"
        : network.Profile is not null ? "Saved"
        : network.Secured ? "Secured" : "Open";

    public string ActionText => Connected ? "Disconnect" : "Connect";

    [ObservableProperty]
    public partial bool Busy { get; set; }

    [RelayCommand]
    private Task ToggleAsync() => owner.ConnectNetworkAsync(this);
}

/// <summary>A paired Bluetooth device. Headsets can be connected; others only show their state.</summary>
public sealed partial class BluetoothItem : ObservableObject
{
    private readonly ControlCenterViewModel _owner;

    internal BluetoothItem(ControlCenterViewModel owner, BluetoothHeadset headset)
    {
        _owner = owner;
        Headset = headset;
        Name = headset.Name;
        Connected = headset.Connected;
        IsAudio = true;
        Detail = headset.Connected
            ? headset.Battery is { } battery ? $"Connected · {battery}%" : "Connected"
            : "Not connected";
    }

    internal BluetoothItem(ControlCenterViewModel owner, PairedDevice device)
    {
        _owner = owner;
        Name = device.Name;
        Connected = device.Connected;
        IsAudio = device.IsAudio;
        Detail = device.Connected ? "Connected" : "Paired";
    }

    internal BluetoothHeadset? Headset { get; }

    public string Name { get; }

    [ObservableProperty]
    public partial string Detail { get; set; }

    public bool Connected { get; }

    public bool IsAudio { get; }

    public bool CanToggle => Headset is not null;

    public string ActionText => Connected ? "Disconnect" : "Connect";

    [ObservableProperty]
    public partial bool Busy { get; set; }

    [RelayCommand]
    private Task ToggleAsync() => _owner.ToggleDeviceAsync(this);
}

/// <summary>One program playing on the default output, with its own volume.</summary>
/// <remarks>
/// Written only after the user touched the slider (<see cref="Arm"/>), like <see cref="DisplayItem"/>, and
/// through <see cref="LatestWins{T}"/>, like the master volume.
/// </remarks>
public sealed partial class AppVolumeItem : ObservableObject
{
    private readonly LatestWins<float> _writer;
    private double _volume;
    private bool _muted;
    private bool _armed;

    public AppVolumeItem(AppAudio app)
    {
        Key = app.Key;
        Name = app.Name;
        Active = app.Active;
        _volume = Math.Round(app.Volume * 100);
        _muted = app.Muted;
        _writer = new LatestWins<float>(v => SystemAudio.SetAppVolume(Key, v));
    }

    public string Key { get; }

    public string Name { get; }

    /// <summary>Playing right now; idle sessions are listed after the active ones.</summary>
    public bool Active { get; }

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, Math.Clamp(Math.Round(value), 0, 100))) return;
            OnPropertyChanged(nameof(VolumeText));
            if (!_armed) return;

            _writer.Post((float)(_volume / 100));
            if (Muted) Muted = false;
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value)) return;
            OnPropertyChanged(nameof(VolumeText));
            if (!_armed) return;

            var key = Key;
            _ = Task.Run(() =>
            {
                try
                {
                    SystemAudio.SetAppMuted(key, value);
                }
                catch (Exception)
                {
                    // The program may have ended in between.
                }
            });
        }
    }

    public string VolumeText => Muted ? "muted" : $"{Math.Round(Volume)}%";

    public void Arm() => _armed = true;

    [RelayCommand]
    private void ToggleMute()
    {
        Arm();
        Muted = !Muted;
    }
}

/// <summary>A playback device in the output list.</summary>
public sealed partial class OutputItem(ControlCenterViewModel owner, OutputDevice device, string name, bool selected)
    : ObservableObject
{
    internal OutputDevice Device { get; } = device;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool Selected { get; set; } = selected;

    [RelayCommand]
    private void Choose() => owner.SelectOutput(this);
}

/// <summary>One slider per display, 0–100 % whatever range the display reports.</summary>
/// <remarks>
/// <para>
/// <b>Writes only after the user touched it</b> (<see cref="Arm"/>, called by the window on pointer or
/// key input). A slider can push a value back while its bindings settle; an offscreen render set both
/// test monitors to 0 that way. Brightness is too visible to trust binding order with.
/// </para>
/// <para>The range is normalized for the same reason: no bound Minimum/Maximum on the slider.</para>
/// </remarks>
public sealed partial class DisplayItem : ObservableObject, IDisposable
{
    private readonly DisplayBrightness? _display;
    private bool _disposed;
    private bool _armed;
    private double _value;

    public DisplayItem(DisplayBrightness display)
    {
        _display = display;
        Name = display.Name;
        Spot = display.Spot;
        Layout = display.Layout;
        var span = Math.Max(1, display.Maximum - display.Minimum);
        _value = Math.Round((display.Current - display.Minimum) * 100.0 / span);
    }

    /// <summary>Where this display sits; with the map, what tells five sliders apart.</summary>
    public DisplaySpot? Spot { get; }

    public IReadOnlyList<DisplaySpot> Layout { get; } = [];

    /// <summary>More than one display: show the map and the label instead of the sun.</summary>
    public bool HasSeveral => Layout.Count > 1;

    public bool HasOne => !HasSeveral;

    /// <summary>"Left · DELL U2720Q", "Top right · Display 3", "Middle · LG ULTRAGEAR · primary".</summary>
    public string Label => string.Join(" · ", new[] { Spot?.Position, Name, Spot?.IsPrimary == true ? "primary" : null }
        .Where(p => !string.IsNullOrEmpty(p)));

    private DisplayItem(string name)
    {
        Name = name;
        IsPlaceholder = true;
    }

    /// <summary>A reserved row while DDC/CI is read.</summary>
    public static DisplayItem Placeholder(int number) => new($"Display {number}");

    public string Name { get; }

    public bool IsPlaceholder { get; }

    public bool IsReal => !IsPlaceholder;

    public double Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, Math.Clamp(Math.Round(value), 0, 100))) return;
            OnPropertyChanged(nameof(ValueText));

            if (_armed && !_disposed && _display is { } display)
            {
                var device = display.Minimum + (int)Math.Round(_value / 100 * (display.Maximum - display.Minimum));
                Brightness.Set(display, device);
            }
        }
    }

    public string ValueText => IsPlaceholder ? "" : $"{Math.Round(Value)}%";

    /// <summary>The user touched the slider; from now on changes are written.</summary>
    public void Arm() => _armed = true;

    public void Dispose() => _disposed = true;
}

/// <summary>Writes only the latest value, on a background thread, one after another.</summary>
internal sealed class LatestWins<T>(Action<T> write)
{
    private readonly Lock _gate = new();
    private T _pending = default!;
    private bool _hasPending;
    private bool _running;

    public void Post(T value)
    {
        lock (_gate)
        {
            _pending = value;
            _hasPending = true;
            if (_running) return;
            _running = true;
        }

        _ = Task.Run(Drain);
    }

    private void Drain()
    {
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (!_hasPending)
                {
                    _running = false;
                    return;
                }

                value = _pending;
                _hasPending = false;
            }

            try
            {
                write(value);
            }
            catch (Exception)
            {
                // The next set tries again.
            }
        }
    }
}
