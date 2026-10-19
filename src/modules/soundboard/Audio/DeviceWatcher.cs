using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>What changed among the audio devices.</summary>
/// <param name="DeviceId">The affected device; for a new default device its id.</param>
/// <param name="DefaultChanged">Set when Windows switched the default device for <paramref name="Flow"/>.</param>
internal readonly record struct DeviceChange(string? DeviceId, DataFlow? Flow, bool DefaultChanged);

/// <summary>Reports when Windows adds, removes or disables audio devices or switches the default.</summary>
/// <remarks>
/// <para>
/// <b>Until 0.9.0 the soundboard listened to none of this.</b> An unplugged headset, a disconnected Bluetooth
/// headphone, a reloaded VB-CABLE driver: the branch behind it died, and the tile kept saying "cable ready"
/// while nothing reached voice chat.
/// </para>
/// <para>
/// Callbacks come on an audio service thread. Nothing is done there except passing on: querying devices from
/// such a callback can block the service. The receiver records the change and rebuilds with a delay.
/// </para>
/// </remarks>
internal sealed class DeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Action<DeviceChange> _changed;
    private bool _registered;

    public DeviceWatcher(Action<DeviceChange> changed)
    {
        _changed = changed;
        _registered = _enumerator.RegisterEndpointNotificationCallback(this) == 0;
    }

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        Raise(new DeviceChange(deviceId, null, false));

    public void OnDeviceAdded(string pwstrDeviceId) =>
        Raise(new DeviceChange(pwstrDeviceId, null, false));

    public void OnDeviceRemoved(string deviceId) =>
        Raise(new DeviceChange(deviceId, null, false));

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        // Windows reports this per role. Only the role AudioDevices.Resolve uses is followed,
        // otherwise one switch would cause three rebuilds.
        if (role != Role.Multimedia) return;
        Raise(new DeviceChange(defaultDeviceId, flow, true));
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Fires on every volume and name change. Irrelevant for the branches.
    }

    private void Raise(DeviceChange change)
    {
        try
        {
            _changed(change);
        }
        catch (Exception)
        {
            // An exception must not propagate into the audio service.
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch (Exception) { }
            _registered = false;
        }

        try { _enumerator.Dispose(); } catch (Exception) { }
    }
}
