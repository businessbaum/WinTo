using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MultiTool.Abstractions;
using MultiTool.Abstractions.Bluetooth;

namespace MultiTool.Modules.BluetoothAudio;

/// <summary>A paired Bluetooth headset with its playback endpoints.</summary>
public sealed partial class HeadsetRow : ObservableObject
{
    private readonly Func<HeadsetRow, bool, Task> _request;

    internal HeadsetRow(Guid containerId, Func<HeadsetRow, bool, Task> request)
    {
        ContainerId = containerId;
        _request = request;
    }

    internal Guid ContainerId { get; }

    /// <summary>A device usually has two endpoints: playback and hands-free.</summary>
    internal List<string> EndpointIds { get; } = [];

    [ObservableProperty] public partial string Name { get; set; } = "";

    [ObservableProperty] public partial bool IsConnected { get; set; }

    [ObservableProperty] public partial string StateText { get; set; } = "";

    [ObservableProperty] public partial string BatteryText { get; set; } = "";

    [ObservableProperty] public partial bool HasBattery { get; set; }

    [ObservableProperty] public partial bool IsBusy { get; set; }

    [RelayCommand]
    private Task ConnectAsync() => _request(this, true);

    [RelayCommand]
    private Task DisconnectAsync() => _request(this, false);
}

/// <summary>Connects, disconnects and keeps an eye on Bluetooth headphones.</summary>
/// <remarks>
/// <para>
/// The constructor touches no devices; the first enumeration comes with <see cref="ViewActivated"/>
/// via <c>Task.Run</c>.
/// </para>
/// <para>
/// The <see cref="EndpointWatcher"/> reports every state change, including ones not started here: if
/// the AirPods jump to the iPhone it shows in the history, and "Connect" brings them back.
/// </para>
/// </remarks>
public sealed partial class BluetoothAudioViewModel : ObservableObject, IDisposable
{
    private readonly IModuleContext _context;
    private readonly Dictionary<Guid, bool> _lastState = [];

    private EndpointWatcher? _watcher;
    private SwitchToast? _toast;
    private bool _dirty;
    private bool _refreshing;
    private bool _baseline;

    // Set until the end of the constructor: OnNotifySwitchChanged calls Save(), which must not fire
    // while the stored value is applied.
    private readonly bool _loading = true;

    public BluetoothAudioViewModel(IModuleContext context)
    {
        _context = context;

        NotifySwitch = context.Settings.Load<BluetoothAudioSettings>().NotifyOnSwitch;

        _loading = false;
    }

    /// <summary>Whether a notification appears at the bottom right on connection changes.</summary>
    [ObservableProperty] public partial bool NotifySwitch { get; set; }

    partial void OnNotifySwitchChanged(bool value)
    {
        if (_loading) return;
        _context.Settings.Save(new BluetoothAudioSettings { NotifyOnSwitch = value });
    }

    public ObservableCollection<HeadsetRow> Devices { get; } = [];

    /// <summary>The latest connection changes, newest first.</summary>
    public ObservableCollection<string> Events { get; } = [];

    [ObservableProperty] public partial bool HasDevices { get; set; }

    [ObservableProperty] public partial bool HasEvents { get; set; }

    /// <summary>Shown only after the first enumeration, or "no devices" would flash.</summary>
    [ObservableProperty] public partial bool Loaded { get; set; }

    [ObservableProperty] public partial string? Message { get; set; }

    [ObservableProperty] public partial bool HasMessage { get; set; }

    partial void OnMessageChanged(string? value) => HasMessage = value is { Length: > 0 };

    public void ViewActivated()
    {
        if (_watcher is null)
        {
            try
            {
                _watcher = new EndpointWatcher();
                _watcher.Changed += OnEndpointsChanged;
            }
            catch (Exception ex)
            {
                _context.Log.Warn($"Endpoint monitoring unavailable: {ex.Message}");
            }
        }

        _ = RefreshAsync();
    }

    public void ViewDeactivated()
    {
        // Nothing to pause: monitoring is the module's purpose and runs while it is loaded, so the
        // notification arrives when the AirPods have switched elsewhere.
    }

    private void OnEndpointsChanged() => Dispatcher.UIThread.Post(() => _ = RefreshAsync());

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            _dirty = true;
            return;
        }

        _refreshing = true;
        try
        {
            var (endpoints, devices) = await Task.Run(static () =>
                (AudioEndpoints.Snapshot(), BluetoothDevices.Snapshot()));

            Apply(endpoints, devices);
            Loaded = true;
        }
        catch (Exception ex)
        {
            Message = $"Device list unreadable: {ex.Message}";
            _context.Log.Error("Enumerating endpoints failed.", ex);
        }
        finally
        {
            _refreshing = false;

            // Another change arrived during the run; catch up once.
            if (_dirty)
            {
                _dirty = false;
                _ = RefreshAsync();
            }
        }
    }

    private void Apply(List<EndpointInfo> endpoints, Dictionary<Guid, BluetoothInfo> devices)
    {
        var byContainer = endpoints
            .Where(e => e.ContainerId != Guid.Empty && devices.ContainsKey(e.ContainerId))
            .GroupBy(e => e.ContainerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Remove vanished rows, refresh existing ones in place; rebuilding the list would make
        // pressed buttons and focus jump.
        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!byContainer.ContainsKey(Devices[i].ContainerId)) Devices.RemoveAt(i);
        }

        foreach (var (container, group) in byContainer)
        {
            var info = devices[container];
            var connected = group.Any(e => e.State == AudioEndpoints.DeviceStateActive);

            var row = Devices.FirstOrDefault(r => r.ContainerId == container);
            if (row is null)
            {
                row = new HeadsetRow(container, RequestAsync);
                Devices.Add(row);
            }

            row.EndpointIds.Clear();
            row.EndpointIds.AddRange(group.Select(e => e.Id));

            row.Name = info.Name is { Length: > 0 } name ? name : CleanName(group[0].Name);
            row.IsConnected = connected;
            row.StateText = connected ? "Connected" : "Disconnected";
            row.HasBattery = connected && info.Battery is not null;
            row.BatteryText = row.HasBattery ? $"Battery {info.Battery}%" : "";

            NoteTransition(container, row.Name, connected);
        }

        HasDevices = Devices.Count > 0;
        _baseline = true;
    }

    /// <summary>Connection changes into the history, from the second state on.</summary>
    private void NoteTransition(Guid container, string name, bool connected)
    {
        var known = _lastState.TryGetValue(container, out var was);
        _lastState[container] = connected;

        if (!_baseline || !known || was == connected) return;

        Events.Insert(0, $"{DateTime.Now:HH:mm}  {name} {(connected ? "connected" : "disconnected")}");
        while (Events.Count > 8) Events.RemoveAt(Events.Count - 1);
        HasEvents = true;

        if (!NotifySwitch) return;

        try
        {
            _toast ??= new SwitchToast();
            _toast.Report(name, connected ? "Connected" : "Disconnected");
        }
        catch (Exception ex)
        {
            // Without a notification the change is still in the history.
            _context.Log.Warn($"Notification could not be shown: {ex.Message}");
        }
    }

    private async Task RequestAsync(HeadsetRow row, bool connect)
    {
        if (row.IsBusy) return;

        row.IsBusy = true;
        Message = null;
        try
        {
            // The driver blocks until the connection is up or failed, seconds when connecting; hence
            // Task.Run, never the UI thread.
            var ids = row.EndpointIds.ToList();
            var ok = await Task.Run(() =>
            {
                var any = false;
                foreach (var id in ids) any |= AudioEndpoints.Request(id, connect);
                return any;
            });

            if (!ok)
            {
                Message = connect
                    ? $"\"{row.Name}\" could not be connected. Switched on and in range?"
                    : $"\"{row.Name}\" could not be disconnected.";
            }
        }
        finally
        {
            row.IsBusy = false;
        }

        await RefreshAsync();
    }

    private static string CleanName(string endpointName)
    {
        // "Headphones (AirPods Pro)" becomes the part in parentheses.
        var open = endpointName.IndexOf('(');
        var close = endpointName.LastIndexOf(')');
        return open >= 0 && close > open
            ? endpointName[(open + 1)..close]
            : endpointName;
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnEndpointsChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _toast?.Close();
        _toast = null;
    }
}

/// <summary>What this module stores in its <c>settings.json</c>.</summary>
public sealed class BluetoothAudioSettings
{
    public bool NotifyOnSwitch { get; set; } = true;
}
