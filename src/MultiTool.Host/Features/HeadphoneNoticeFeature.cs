using Avalonia.Threading;
using MultiTool.Abstractions.Bluetooth;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;
using MultiTool.Host.Views;
using MultiTool.UI;

namespace MultiTool.Host.Features;

/// <summary>A card when Bluetooth headphones connect or disconnect, with the battery level.</summary>
/// <remarks>
/// <para>
/// From the former Bluetooth audio module, which could only tell while it was open. Connecting itself is in
/// the control center.
/// </para>
/// <para>
/// <b>Event-driven, no polling:</b> Core Audio reports endpoint state changes (<see cref="EndpointWatcher"/>).
/// One change comes as several callbacks (two endpoints per headset, added and state changed), so the read
/// waits for a short quiet period. The first read only sets the baseline: starting the app must not announce
/// every device that is already connected.
/// </para>
/// </remarks>
public sealed class HeadphoneNoticeFeature(ShellViewModel shell) : Feature(shell)
{
    private const string HeadphonesPath =
        "M12 3a9 9 0 0 0-9 9v7a2 2 0 0 0 2 2h2a2 2 0 0 0 2-2v-4a2 2 0 0 0-2-2H5v-1a7 7 0 0 1 14 0v1h-2a2 2 0 0 0-2 2v4a2 2 0 0 0 2 2h2a2 2 0 0 0 2-2v-7a9 9 0 0 0-9-9Z";

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(700);

    private EndpointWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private Dictionary<Guid, bool>? _known;
    private int _generation;

    public override string Id => "headphone-notice";

    public override string Name => "Headphone notice";

    public override string Description => "A card when Bluetooth headphones connect or disconnect, with the battery level.";

    public override string Group => "Sound";

    protected override string IconPath => HeadphonesPath;

    protected override bool ReadEnabled() => Settings.HeadphoneNotice;

    protected override void StoreEnabled(bool value) => Settings.HeadphoneNotice = value;

    protected override void Start()
    {
        _known = null;
        _debounce = new DispatcherTimer { Interval = Quiet };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = ReadAsync();
        };

        _watcher = new EndpointWatcher();
        _watcher.Changed += OnChanged;

        Detail = "On";
        _ = ReadAsync();
    }

    protected override void Stop()
    {
        _generation++;

        if (_watcher is not null)
        {
            _watcher.Changed -= OnChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _debounce?.Stop();
        _debounce = null;
        _known = null;
        Detail = "Off";
    }

    /// <summary>From a COM thread.</summary>
    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_debounce is null) return;

        _debounce.Stop();
        _debounce.Start();
    });

    private async Task ReadAsync()
    {
        var generation = _generation;
        List<BluetoothHeadset> headsets;

        try
        {
            headsets = await Task.Run(BluetoothHeadsets.Snapshot);
        }
        catch (Exception ex)
        {
            new ModuleLog("features").Warn($"Headphones could not be read: {ex.Message}");
            return;
        }

        // Switched off while reading.
        if (generation != _generation) return;

        var previous = _known;
        _known = headsets.ToDictionary(h => h.ContainerId, h => h.Connected);
        if (previous is null) return;

        foreach (var headset in headsets)
        {
            if (!previous.TryGetValue(headset.ContainerId, out var was) || was == headset.Connected) continue;

            var detail = headset.Connected
                ? headset.Battery is { } battery ? $"Connected · battery {battery}%" : "Connected"
                : "Disconnected";

            Toast.Show(NoticeKind.Devices, headset.Name, detail, IconCache.Get(HeadphonesPath));
        }
    }
}
