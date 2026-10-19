using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MultiTool.Modules.Soundboard.Audio;

/// <summary>Sets the Windows default recording device.</summary>
/// <remarks>
/// <para>
/// Why: Discord and many other programs use the Windows default microphone without offering their own
/// device choice. Switching between headset and cable would otherwise mean a trip to Windows settings.
/// </para>
/// <para>
/// Why so awkward: Windows offers <b>no</b> documented interface for it. The only way is <c>IPolicyConfig</c>, an
/// undocumented COM interface also used by SoundSwitch and AudioSwitcher. Stable since Windows 7 but not
/// guaranteed, so everything here is guarded and a failure is reported instead of thrown.
/// </para>
/// <para>
/// The method order in the interface is <b>binding</b>: COM addresses methods by vtable position, not name. The
/// ten methods before <c>SetDefaultEndpoint</c> are never called and only keep the position right.
/// </para>
/// </remarks>
public static class DefaultDeviceSwitcher
{
    private static readonly Guid PolicyConfigClientClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    /// <summary>Set all three roles so Windows settings and programs agree.</summary>
    private static readonly int[] AllRoles = [0 /* Console */, 1 /* Multimedia */, 2 /* Communications */];

    /// <summary>Is the switcher available on this system?</summary>
    public static bool IsSupported
    {
        get
        {
            try { return Type.GetTypeFromCLSID(PolicyConfigClientClsid) is not null; }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Sets the default recording device. Returns a message on failure.</summary>
    public static bool TrySetDefaultCaptureDevice(string deviceId, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(deviceId) || deviceId == AudioDevice.DefaultId)
        {
            error = "No valid device selected.";
            return false;
        }

        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid);
            if (type is null)
            {
                error = "Windows doesn't provide the required interface.";
                return false;
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IPolicyConfig config)
            {
                error = "The interface could not be reached.";
                return false;
            }

            foreach (var role in AllRoles)
            {
                var hr = config.SetDefaultEndpoint(deviceId, role);
                if (hr < 0)
                {
                    error = $"Windows refused the change (0x{hr:X8}).";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (instance is not null)
            {
                try { Marshal.ReleaseComObject(instance); } catch (Exception) { }
            }
        }
    }

    /// <summary>The id of the current default recording device, or null.</summary>
    public static string? GetDefaultCaptureDeviceId()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return device.ID;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // --- Placeholders: only for the correct vtable position ---
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, out IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr key, IntPtr value);

        // --- the method actually wanted (position 11) ---
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
