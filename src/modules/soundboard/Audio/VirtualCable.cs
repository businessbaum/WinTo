namespace MultiTool.Modules.Soundboard.Audio;

public enum VirtualCableKind
{
    None,
    VbCable,
    VoiceMeeter,
    VirtualAudioCable,
}

/// <summary>
/// A detected virtual cable: the playback device written to, and the recording device the target app
/// (Discord, TeamSpeak, ...) must select.
/// </summary>
public sealed record VirtualCableInfo(
    VirtualCableKind Kind,
    string ProductName,
    AudioDevice RenderDevice,
    AudioDevice? CaptureDevice)
{
    public string CaptureDeviceName => CaptureDevice?.Name ?? "(no matching recording device found)";
}

/// <summary>
/// Detects installed virtual audio cables. VB-Cable takes precedence; VoiceMeeter and Virtual Audio Cable are
/// accepted as alternatives so their users needn't install VB-Cable too.
/// </summary>
public static class VirtualCableDetector
{
    public static VirtualCableInfo? Detect() => Detect(AudioDevices.Render(), AudioDevices.Capture());

    public static VirtualCableInfo? Detect(IReadOnlyList<AudioDevice> render, IReadOnlyList<AudioDevice> capture)
    {
        foreach (var kind in new[] { VirtualCableKind.VbCable, VirtualCableKind.VoiceMeeter, VirtualCableKind.VirtualAudioCable })
        {
            var device = render.FirstOrDefault(d => Matches(kind, d.Name, renderSide: true));
            if (device is null) continue;

            var counterpart = FindCounterpart(kind, device, capture);
            return new VirtualCableInfo(kind, ProductName(kind), device, counterpart);
        }

        return null;
    }

    public static bool IsVirtualCableDevice(string name) =>
        Matches(VirtualCableKind.VbCable, name, true) || Matches(VirtualCableKind.VbCable, name, false) ||
        Matches(VirtualCableKind.VoiceMeeter, name, true) || Matches(VirtualCableKind.VoiceMeeter, name, false) ||
        Matches(VirtualCableKind.VirtualAudioCable, name, true) || Matches(VirtualCableKind.VirtualAudioCable, name, false);

    public static string ProductName(VirtualCableKind kind) => kind switch
    {
        VirtualCableKind.VbCable => "VB-CABLE",
        VirtualCableKind.VoiceMeeter => "VoiceMeeter",
        VirtualCableKind.VirtualAudioCable => "Virtual Audio Cable",
        _ => "unknown",
    };

    private static bool Matches(VirtualCableKind kind, string name, bool renderSide)
    {
        var n = name.ToLowerInvariant();

        // Note: from the app's view the playback device is the cable's "Input".
        var sideToken = renderSide ? "input" : "output";

        return kind switch
        {
            VirtualCableKind.VbCable => n.Contains("cable") && n.Contains("vb-audio") && n.Contains(sideToken),
            VirtualCableKind.VoiceMeeter => n.Contains("voicemeeter") && n.Contains(sideToken),
            VirtualCableKind.VirtualAudioCable => n.Contains("virtual audio cable") && (renderSide ? !n.Contains("out") : n.Contains("out")),
            _ => false,
        };
    }

    /// <summary>
    /// Finds the counterpart on the recording side, primarily by the driver name in parentheses:
    /// "CABLE Input (VB-Audio Virtual Cable)" belongs to "CABLE Output (VB-Audio Virtual Cable)", including the
    /// A/B/C/D variants.
    /// </summary>
    private static AudioDevice? FindCounterpart(VirtualCableKind kind, AudioDevice render, IReadOnlyList<AudioDevice> capture)
    {
        var driver = DriverSuffix(render.Name);
        if (driver is not null)
        {
            var paired = capture.FirstOrDefault(d =>
                string.Equals(DriverSuffix(d.Name), driver, StringComparison.OrdinalIgnoreCase) &&
                Matches(kind, d.Name, renderSide: false));
            if (paired is not null) return paired;
        }

        return capture.FirstOrDefault(d => Matches(kind, d.Name, renderSide: false));
    }

    private static string? DriverSuffix(string name)
    {
        var open = name.LastIndexOf('(');
        var close = name.LastIndexOf(')');
        return open >= 0 && close > open ? name[(open + 1)..close].Trim() : null;
    }
}
