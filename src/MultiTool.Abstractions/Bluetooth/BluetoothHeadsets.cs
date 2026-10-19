namespace MultiTool.Abstractions.Bluetooth;

/// <summary>A paired Bluetooth audio device with its playback endpoints.</summary>
/// <param name="Battery">Charge in percent, only while connected.</param>
/// <param name="EndpointIds">Usually two: stereo playback and hands-free.</param>
public sealed record BluetoothHeadset(
    Guid ContainerId, string Name, bool Connected, byte? Battery, IReadOnlyList<string> EndpointIds);

/// <summary>Bluetooth headphones and speakers: list, connect, disconnect. Since 0.11.0.</summary>
/// <remarks>
/// Joins <see cref="AudioEndpoints"/> and <see cref="BluetoothDevices"/> by container id: an endpoint
/// whose container also has a <c>BTHENUM</c> node is Bluetooth. Every call blocks (COM, the driver);
/// never on the UI thread.
/// </remarks>
public static class BluetoothHeadsets
{
    public static List<BluetoothHeadset> Snapshot()
    {
        var endpoints = AudioEndpoints.Snapshot();
        var devices = BluetoothDevices.Snapshot();

        return endpoints
            .Where(e => e.ContainerId != Guid.Empty && devices.ContainsKey(e.ContainerId))
            .GroupBy(e => e.ContainerId)
            .Select(group =>
            {
                var info = devices[group.Key];
                var connected = group.Any(e => e.State == AudioEndpoints.DeviceStateActive);
                var name = info.Name is { Length: > 0 } n ? n : CleanName(group.First().Name);
                return new BluetoothHeadset(group.Key, name, connected,
                    connected ? info.Battery : null, [.. group.Select(e => e.Id)]);
            })
            .OrderByDescending(h => h.Connected)
            .ThenBy(h => h.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Asks the driver to connect or disconnect. Takes seconds when connecting.</summary>
    /// <returns><c>true</c> if the driver accepted it for at least one endpoint.</returns>
    public static bool SetConnected(BluetoothHeadset headset, bool connect)
    {
        var any = false;
        foreach (var id in headset.EndpointIds) any |= AudioEndpoints.Request(id, connect);
        return any;
    }

    /// <summary>"Headphones (AirPods Pro)" becomes the part in parentheses.</summary>
    public static string CleanName(string endpointName)
    {
        var open = endpointName.IndexOf('(');
        var close = endpointName.LastIndexOf(')');
        return open >= 0 && close > open ? endpointName[(open + 1)..close] : endpointName;
    }
}
