using NAudio.CoreAudioApi;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>An audio endpoint, decoupled from NAudio's COM objects.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsCapture, bool IsSystemDefault)
{
    public const string DefaultId = "__default__";

    public static AudioDevice SystemDefaultRender { get; } =
        new(DefaultId, "System default", false, true);

    public static AudioDevice SystemDefaultCapture { get; } =
        new(DefaultId, "System default", true, true);
}

public static class AudioDevices
{
    public static List<AudioDevice> Render() => Enumerate(DataFlow.Render);

    public static List<AudioDevice> Capture() => Enumerate(DataFlow.Capture);

    private static List<AudioDevice> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception)
            {
                // No default device (e.g. no sound card at all); not an error.
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                {
                    result.Add(new AudioDevice(
                        device.ID,
                        SafeName(device),
                        flow == DataFlow.Capture,
                        device.ID == defaultId));
                }
            }
        }
        catch (Exception)
        {
            // If the audio stack misbehaves, an empty list beats a crash.
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static string SafeName(MMDevice device)
    {
        try { return device.FriendlyName; }
        catch (Exception) { return device.ID; }
    }

    /// <summary>
    /// Gets the MMDevice for an id. Falls back to the system default if the id is empty or the device is gone
    /// (USB headset unplugged).
    /// </summary>
    public static MMDevice? Resolve(string? id, bool capture)
    {
        var enumerator = new MMDeviceEnumerator();
        try
        {
            var flow = capture ? DataFlow.Capture : DataFlow.Render;

            if (!string.IsNullOrWhiteSpace(id) && id != AudioDevice.DefaultId)
            {
                try
                {
                    var device = enumerator.GetDevice(id);
                    if (device.State == DeviceState.Active) return device;
                    device.Dispose();
                }
                catch (Exception)
                {
                    // Device gone: use the default.
                }
            }

            try { return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
            catch (Exception) { return null; }
        }
        finally
        {
            enumerator.Dispose();
        }
    }
}
