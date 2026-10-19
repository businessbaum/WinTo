using System.Runtime.InteropServices;

namespace MultiTool.Host.ControlCenter;

/// <summary>An output device for the control center's picker.</summary>
public sealed record OutputDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One program's audio in the mixer: all its sessions on the default output together.</summary>
/// <param name="Key">The process name, or "system" for system sounds; identifies the row across reads.</param>
public sealed record AppAudio(string Key, string Name, float Volume, bool Muted, bool Active);

/// <summary>Volume, mute and device, as read on open.</summary>
public sealed record AudioState(
    float Volume, bool Muted, string? DefaultOutputId,
    IReadOnlyList<OutputDevice> Outputs, bool HasMicrophone, bool MicrophoneMuted);

/// <summary>Windows volume via Core Audio, without NAudio.</summary>
/// <remarks>
/// <para>
/// No NAudio because this is the shell; NAudio belongs to modules and must not load at startup.
/// The interfaces are documented COM.
/// </para>
/// <para>
/// Switching the default device is not: only <c>IPolicyConfig</c> (undocumented, unchanged since
/// Windows 7, also used by the soundboard). The placeholders keep the vtable positions; only
/// <c>SetDefaultEndpoint</c> is called.
/// </para>
/// <para>
/// Every call creates and releases its own enumerator: a millisecond, and nothing is held while
/// the control center is closed.
/// </para>
/// </remarks>
internal static class SystemAudio
{
    private const int Render = 0;
    private const int Capture = 1;
    private const int Multimedia = 1;
    private const int StateActive = 1;

    private static readonly Guid IidEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly Guid PolicyConfigClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    /// <summary>Who changes the volume here. Windows passes it on to callbacks.</summary>
    private static Guid _context = Guid.NewGuid();

    public static AudioState Read()
    {
        var enumerator = CreateEnumerator();
        try
        {
            float volume = 0;
            var muted = false;
            string? defaultId = null;

            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out var output) == 0)
            {
                output.GetId(out defaultId);
                WithVolume(output, endpoint =>
                {
                    endpoint.GetMasterVolumeLevelScalar(out volume);
                    endpoint.GetMute(out var m);
                    muted = m != 0;
                });
                Marshal.ReleaseComObject(output);
            }

            var outputs = new List<OutputDevice>();
            if (enumerator.EnumAudioEndpoints(Render, StateActive, out var collection) == 0)
            {
                collection.GetCount(out var count);
                for (var i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0) continue;
                    device.GetId(out var id);
                    outputs.Add(new OutputDevice(id, NameOf(device) ?? id));
                    Marshal.ReleaseComObject(device);
                }

                Marshal.ReleaseComObject(collection);
            }

            var hasMicrophone = false;
            var microphoneMuted = false;
            if (enumerator.GetDefaultAudioEndpoint(Capture, Multimedia, out var input) == 0)
            {
                hasMicrophone = true;
                WithVolume(input, endpoint =>
                {
                    endpoint.GetMute(out var m);
                    microphoneMuted = m != 0;
                });
                Marshal.ReleaseComObject(input);
            }

            return new AudioState(volume, muted, defaultId,
                [.. outputs.OrderBy(o => o.Name, StringComparer.CurrentCulture)], hasMicrophone, microphoneMuted);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public static void SetVolume(float scalar) =>
        WithDefault(Render, endpoint => endpoint.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0, 1), ref _context));

    public static void SetMuted(bool muted) =>
        WithDefault(Render, endpoint => endpoint.SetMute(muted ? 1 : 0, ref _context));

    public static void SetMicrophoneMuted(bool muted) =>
        WithDefault(Capture, endpoint => endpoint.SetMute(muted ? 1 : 0, ref _context));

    /// <summary>Mutes or unmutes <b>every</b> active microphone, not just the default one.</summary>
    /// <remarks>A headset and a webcam both record; muting only the default leaves the other open.</remarks>
    /// <returns>How many were switched.</returns>
    public static int SetAllMicrophonesMuted(bool muted)
    {
        var count = 0;
        ForEachActive(Capture, endpoint =>
        {
            if (endpoint.SetMute(muted ? 1 : 0, ref _context) == 0) count++;
        });
        return count;
    }

    /// <summary>Whether the default microphone is muted; <c>null</c> without one.</summary>
    public static bool? MicrophoneMuted()
    {
        bool? result = null;
        WithDefault(Capture, endpoint =>
        {
            endpoint.GetMute(out var muted);
            result = muted != 0;
        });
        return result;
    }

    /// <summary>Makes the next active output the default, in name order.</summary>
    /// <returns>The new device's name, or <c>null</c> with fewer than two outputs or on failure.</returns>
    public static string? CycleOutput()
    {
        var state = Read();
        if (state.Outputs.Count < 2) return null;

        var index = state.Outputs.ToList().FindIndex(o => o.Id == state.DefaultOutputId);
        var next = state.Outputs[(index + 1) % state.Outputs.Count];
        return SetDefaultOutput(next.Id) is null ? next.Name : null;
    }

    // ------------------------------------------------------------------ Per-app volume

    private static readonly Guid IidSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>Programs playing on the default output, one row per program.</summary>
    /// <remarks>
    /// Windows keeps a session per process and output; browsers and chat apps run several processes, so rows
    /// are grouped by process name. Expired sessions (the program ended) are left out.
    /// </remarks>
    public static List<AppAudio> Apps()
    {
        var rows = new Dictionary<string, AppAudio>(StringComparer.OrdinalIgnoreCase);

        ForEachSession(names: true, (key, name, control, volume) =>
        {
            control.GetState(out var state);
            volume.GetMasterVolume(out var level);
            volume.GetMute(out var muted);

            var row = new AppAudio(key, name, level, muted != 0, state == 1);
            rows[key] = rows.TryGetValue(key, out var existing)
                ? existing with
                {
                    Volume = Math.Max(existing.Volume, row.Volume),
                    Muted = existing.Muted && row.Muted,
                    Active = existing.Active || row.Active,
                }
                : row;
        });

        return [.. rows.Values
            .OrderByDescending(a => a.Active)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>Sets every session of a program.</summary>
    /// <remarks>Without display names: reading a program's description costs milliseconds per process, and
    /// this runs for every step of a slider.</remarks>
    public static void SetAppVolume(string key, float level) =>
        ForEachSession(names: false, (k, _, _, volume) =>
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                volume.SetMasterVolume(Math.Clamp(level, 0, 1), ref _context);
        });

    public static void SetAppMuted(string key, bool muted) =>
        ForEachSession(names: false, (k, _, _, volume) =>
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                volume.SetMute(muted ? 1 : 0, ref _context);
        });

    private static void ForEachSession(bool names, Action<string, string, IAudioSessionControl2, ISimpleAudioVolume> action)
    {
        var enumerator = CreateEnumerator();
        object? activated = null;
        IAudioSessionEnumerator? sessions = null;
        IMMDevice? device = null;

        try
        {
            if (enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out device) != 0) return;

            var iid = IidSessionManager2;
            if (device.Activate(ref iid, 1, 0, out activated) != 0) return;
            if (((IAudioSessionManager2)activated).GetSessionEnumerator(out sessions) != 0) return;

            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var session) != 0) continue;

                try
                {
                    if (session is not IAudioSessionControl2 control || session is not ISimpleAudioVolume volume) continue;

                    control.GetState(out var state);
                    if (state == 2) continue; // expired

                    var (key, name) = Identify(control, names);
                    if (key is not null) action(key, name, control, volume);
                }
                finally
                {
                    Marshal.ReleaseComObject(session);
                }
            }
        }
        finally
        {
            if (sessions is not null) Marshal.ReleaseComObject(sessions);
            if (activated is not null) Marshal.ReleaseComObject(activated);
            if (device is not null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>Process name as key, the program's description as display name.</summary>
    private static (string? Key, string Name) Identify(IAudioSessionControl2 control, bool names)
    {
        if (control.IsSystemSoundsSession() == 0) return ("system", "System sounds");
        if (control.GetProcessId(out var pid) != 0 || pid == 0) return (null, "");

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (!names) return (name, name);

            string? description = null;
            try
            {
                description = process.MainModule?.FileVersionInfo.FileDescription;
            }
            catch (Exception)
            {
                // An elevated program, read from an unelevated WinTo: the name has to do.
            }

            return (name, description is { Length: > 0 } d ? d : name);
        }
        catch (Exception)
        {
            return (null, "");
        }
    }

    private static void ForEachActive(int flow, Action<IAudioEndpointVolume> action)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.EnumAudioEndpoints(flow, StateActive, out var collection) != 0) return;

            try
            {
                collection.GetCount(out var count);
                for (var i = 0; i < count; i++)
                {
                    if (collection.Item(i, out var device) != 0) continue;
                    WithVolume(device, action);
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? SetDefaultOutput(string deviceId)
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClsid);
            if (type is null) return "Windows does not provide switching.";

            instance = Activator.CreateInstance(type);
            if (instance is not IPolicyConfig config) return "Switching could not be accessed.";

            // All three roles, otherwise settings and programs show different devices.
            foreach (var role in new[] { 0, 1, 2 })
            {
                var hr = config.SetDefaultEndpoint(deviceId, role);
                if (hr < 0) return $"Windows rejected the change (0x{hr:X8}).";
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            if (instance is not null) Marshal.ReleaseComObject(instance);
        }
    }

    // ------------------------------------------------------------------ Helpers

    private static void WithDefault(int flow, Action<IAudioEndpointVolume> action)
    {
        var enumerator = CreateEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, Multimedia, out var device) != 0) return;
            WithVolume(device, action);
            Marshal.ReleaseComObject(device);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static void WithVolume(IMMDevice device, Action<IAudioEndpointVolume> action)
    {
        var iid = IidEndpointVolume;
        if (device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, 0, out var activated) != 0) return;

        try
        {
            action((IAudioEndpointVolume)activated);
        }
        finally
        {
            Marshal.ReleaseComObject(activated);
        }
    }

    private static string? NameOf(IMMDevice device)
    {
        if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;

        try
        {
            var key = FriendlyName;
            if (store.GetValue(ref key, out var value) != 0) return null;

            try
            {
                return value.Vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>CoCreateInstance plus interface cast, as in the Bluetooth module.</summary>
    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = typeof(IMMDeviceEnumerator).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 1, ref iid, out var instance));
        return (IMMDeviceEnumerator)instance;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object instance);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>Just enough PROPVARIANT for a string. 24 bytes on x64.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public nint Pointer;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, nint parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object result);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    /// <summary>Order as in endpointvolume.h; COM addresses by position.</summary>
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint notify);
        [PreserveSig] int UnregisterControlChangeNotify(nint notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute(int mute, ref Guid context);
        [PreserveSig] int GetMute(out int mute);
    }

    /// <summary>Order as in audiopolicy.h; the first two methods are IAudioSessionManager's.</summary>
    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, uint flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, uint flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(nint notification);
        [PreserveSig] int UnregisterSessionNotification(nint notification);
        [PreserveSig] int RegisterDuckNotification(nint sessionId, nint notification);
        [PreserveSig] int UnregisterDuckNotification(nint notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    /// <summary>IAudioSessionControl's nine methods first, then IAudioSessionControl2's five.</summary>
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName(nint name, ref Guid context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(nint notification);
        [PreserveSig] int UnregisterAudioSessionNotification(nint notification);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(int mute, ref Guid context);
        [PreserveSig] int GetMute(out int mute);
    }

    /// <summary>See the class comment and <c>soundboard/Audio/DefaultDeviceSwitcher.cs</c>.</summary>
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint key, out nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
