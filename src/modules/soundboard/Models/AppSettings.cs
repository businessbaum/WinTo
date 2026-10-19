namespace MultiTool.Modules.Soundboard.Models;

/// <summary>Persistent settings, stored as readable JSON in the module's data folder.</summary>
public sealed class AppSettings
{
    // --- Routing -------------------------------------------------------

    /// <summary>Playback device the target app sees as a microphone (usually "CABLE Input").</summary>
    public string? VirtualOutputDeviceId { get; set; }

    /// <summary>Monitor device: your headphones. Separate from the virtual cable.</summary>
    public string? MonitorOutputDeviceId { get; set; }

    /// <summary>Physical microphone passed through into the virtual cable.</summary>
    public string? MicDeviceId { get; set; }

    public bool VirtualOutputEnabled { get; set; } = true;
    public bool MonitorEnabled { get; set; } = true;
    public bool MicPassthroughEnabled { get; set; } = true;

    /// <summary>
    /// Also put your own microphone on the headphones. Off by default: the round trip through WASAPI costs about
    /// 60 ms, and hearing your own echo at that delay makes speaking very unpleasant.
    /// </summary>
    public bool MicMonitorEnabled { get; set; }

    // --- Levels --------------------------------------------------------

    // Slider positions, not gain factors: 1.0 = unchanged (0 dB). VolumeCurve converts.
    public float MasterVolume { get; set; } = 1.00f;
    public float MonitorVolume { get; set; } = 0.85f;
    public float MicVolume { get; set; } = 1.00f;

    /// <summary>Level of the passed-through desktop audio. Separate path, separate slider.</summary>
    public float DesktopVolume { get; set; } = 0.85f;

    // --- Desktop audio -------------------------------------------------

    /// <summary>
    /// Hotkey for the desktop audio tunnel. The tunnel is deliberately temporary, so a toggle from inside a game
    /// is more useful than a setting.
    /// </summary>
    public Hotkey? DesktopTunnelHotkey { get; set; }

    /// <summary>
    /// Which app is sent, as a <b>process name</b>, not a process id: that changes on every app restart, the name
    /// stays. Empty or <c>null</c> means everything except WinTo.
    /// </summary>
    public string? DesktopTunnelApp { get; set; }

    // --- Audio ---------------------------------------------------------

    /// <summary>
    /// WASAPI buffer in ms. Smaller means lower latency and higher dropout risk. Also sets the capture buffer and
    /// the microphone's allowed backlog, so it counts three times in the mic delay.
    /// </summary>
    public int BufferMilliseconds { get; set; } = 30;

    /// <summary>
    /// Soft clipping protection. Without it WASAPI hard-clips everything above ±1.0, audibly crackling at the
    /// loudest moments.
    ///
    /// <b>Off</b> by default: the protection alters the signal, and as long as nothing clips doing nothing is the
    /// honest default. Turn it on if you hear crackling.
    /// </summary>
    public bool LimiterEnabled { get; set; }

    /// <summary>Upper limit for the PCM cache in RAM.</summary>
    public int CacheBudgetMegabytes { get; set; } = 192;

    /// <summary>Sounds longer than this are streamed from disk instead of cached.</summary>
    public int MaxCacheSeconds { get; set; } = 90;

    // --- Hotkeys -------------------------------------------------------

    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>Swallow bound keys for other programs instead of only listening.</summary>
    public bool SuppressHotkeysInOtherApps { get; set; }

    public Hotkey? StopAllHotkey { get; set; }

    /// <summary>Starting a sound stops all others (monophonic).</summary>
    public bool StopOthersOnPlay { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
