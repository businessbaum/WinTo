using System.Diagnostics;
using MultiTool.Abstractions;
using NAudio.CoreAudioApi;

namespace MultiTool.Modules.SystemOverview;

/// <summary>What can actually be changed from here.</summary>
/// <remarks>
/// <para><b>Only what can be changed with documented means.</b></para>
/// <para>
/// The prominent counterexample is the default playback device, only switchable via
/// <c>IPolicyConfig</c>, an unpublished COM interface whose method order is copied from other projects.
/// If it changed in a future Windows, a call would hit a wrong address and kill the process, which no
/// <c>try</c> catches. A button into Windows settings instead.
/// </para>
/// <para>
/// What works: volume and mute per device (Core Audio), enabling and disabling network adapters
/// (<c>netsh</c>, needs admin rights) and autostart entries (registry, see <see cref="Autostart"/>).
/// </para>
/// </remarks>
/// <remarks>
/// Named <c>SystemControl</c>, not <c>Control</c>: a type of that name in the module namespace would shadow
/// <c>Avalonia.Controls.Control</c> and break <c>ViewActivated(Control view)</c> with a misleading error.
/// </remarks>
internal static class SystemControl
{
    // ------------------------------------------------------------------ Network

    /// <summary>Asks <c>netsh</c> which network connections exist and which are enabled.</summary>
    /// <remarks>
    /// <para>
    /// <b>Two answers in one.</b> The values say whether an adapter is enabled, which .NET can't tell
    /// (disabled and unplugged are both <c>OperationalStatus.Down</c>). The key set says which adapters
    /// Windows treats as connections at all.
    /// </para>
    /// <para>
    /// <c>netsh interface show interface</c> lists exactly the four entries of the "Network Connections"
    /// folder here, while <see cref="System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces"/>
    /// returns 50 (see <c>Devices.IsRealConnection</c>).
    /// </para>
    /// <para>
    /// The output is <b>localized</b>: "Deaktiviert" on German Windows, "Disabled" on English. Both prefixes
    /// are checked, and anything unknown counts as enabled; an adapter wrongly read as off would invite
    /// enabling something already on.
    /// </para>
    /// </remarks>
    public static async Task<Dictionary<string, bool>> ReadConnectionsAsync(CancellationToken cancellationToken)
    {
        var state = new Dictionary<string, bool>(StringComparer.CurrentCultureIgnoreCase);

        try
        {
            var result = await ExternalProcess.RunAsync("netsh",
                ["interface", "show", "interface"], cancellationToken: cancellationToken);

            if (!result.Ok) return state;

            // The header is recognized by the dashes below it, not its wording, which is localized.
            // A header ending up as an adapter name would now decide which adapters are shown, and a
            // state word matching none of the prefixes would make its adapter vanish.
            var afterHeader = false;

            foreach (var line in result.Output.Split('\n'))
            {
                var trimmed = line.Trim();

                if (!afterHeader)
                {
                    afterHeader = trimmed.Length >= 10 && trimmed.All(c => c == '-');
                    continue;
                }

                var columns = line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (columns.Length < 4) continue;

                var admin = columns[0];
                var name = columns[^1];

                // Only "disabled" is recognized positively; everything else counts as on.
                state[name] = !(admin.StartsWith("Deaktiv", StringComparison.OrdinalIgnoreCase)
                             || admin.StartsWith("Disab", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception)
        {
            // Without a netsh answer every adapter counts as enabled; the list shows one detail less.
        }

        return state;
    }

    /// <summary>Enables or disables a network adapter. Needs administrator rights.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static async Task<string?> SetAdapterAsync(
        string name, bool enabled, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ExternalProcess.RunAsync("netsh",
            [
                "interface", "set", "interface",

                // Separate arguments with "name=": an adapter called "VMware Network Adapter VMnet1" would
                // otherwise become four arguments.
                $"name={name}",
                $"admin={(enabled ? "enabled" : "disabled")}",
            ], cancellationToken: cancellationToken);

            if (result.Ok) return null;

            var text = (result.Error + result.Output).Trim();

            // netsh reports missing rights only in its (localized) text; without this the message would
            // be "element not found" and nobody would think of rights.
            return text.Length == 0 || text.Contains("Zugriff", StringComparison.OrdinalIgnoreCase)
                || text.Contains("denied", StringComparison.OrdinalIgnoreCase)
                || text.Contains("erforderlich", StringComparison.OrdinalIgnoreCase)
                    ? "Administrator rights required."
                    : text;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ Sound

    private static MMDeviceEnumerator? _enumerator;
    private static readonly Dictionary<string, MMDevice> Cached = [];

    /// <summary>Sets volume (0…1) and mute of an endpoint.</summary>
    /// <remarks>
    /// <para>
    /// <b>By device id with a cache</b>, not by name and enumeration: this call hangs on a slider and arrives
    /// sixty times a second while dragging; every enumeration is a dozen COM transitions and visibly
    /// stutters.
    /// </para>
    /// <para>
    /// What stays open is returned by <see cref="Release"/>, called from the module's <c>Dispose</c>.
    /// </para>
    /// </remarks>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? SetVolume(string deviceId, double? volume, bool? mute)
    {
        try
        {
            if (!Cached.TryGetValue(deviceId, out var device))
            {
                _enumerator ??= new MMDeviceEnumerator();
                device = _enumerator.GetDevice(deviceId);
                Cached[deviceId] = device;
            }

            if (volume is { } level)
                device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Math.Clamp(level, 0, 1);

            if (mute is { } muted)
                device.AudioEndpointVolume.Mute = muted;

            return null;
        }
        catch (Exception ex)
        {
            // An unplugged device would otherwise stay in the cache and fail on every further drag.
            if (Cached.Remove(deviceId, out var stale))
            {
                try { stale.Dispose(); } catch (Exception) { }
            }

            return ex.Message;
        }
    }

    /// <summary>Releases the open audio devices. Belongs in the module's <c>Dispose</c>.</summary>
    public static void Release()
    {
        foreach (var device in Cached.Values)
        {
            try { device.Dispose(); } catch (Exception) { }
        }

        Cached.Clear();

        try { _enumerator?.Dispose(); } catch (Exception) { }
        _enumerator = null;
    }

    // ------------------------------------------------------------------ Windows

    /// <summary>Opens a Windows UI.</summary>
    /// <remarks>For everything this module deliberately doesn't change itself.</remarks>
    public static void Open(string target, string? arguments = null)
    {
        // Task.Run: the callers are button commands on the UI thread, and Process.Start can take
        // hundreds of ms on first launch (antivirus).
        _ = Task.Run(() =>
        {
            try
            {
                var info = new ProcessStartInfo(target) { UseShellExecute = true };

                // Arguments separately: "rundll32.exe sysdm.cpl,…" would otherwise become a program name with
                // spaces.
                if (arguments is not null) info.Arguments = arguments;

                Process.Start(info);
            }
            catch (Exception)
            {
                // A control panel that won't open is no reason to crash.
            }
        });
    }
}
