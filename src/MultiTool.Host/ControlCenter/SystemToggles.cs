using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MultiTool.Host.ControlCenter;

/// <summary>How the computer is connected right now.</summary>
/// <param name="Summary">A few words for the tile: "Wi-Fi · Home", "Ethernet", "disconnected".</param>
/// <param name="Internet">Whether Windows reaches the internet, not just the local network.</param>
public sealed record NetworkState(string Summary, bool Connected, bool Internet);

/// <summary>Battery, if present.</summary>
public sealed record BatteryState(bool Present, int Percent, bool Charging);

/// <summary>The control center's small switches, a few lines of system calls each.</summary>
internal static partial class SystemToggles
{
    // ------------------------------------------------------------------ Network

    /// <summary>The connection carrying the default route.</summary>
    /// <remarks>
    /// An adapter with a gateway is where traffic actually leaves; the NDIS filter instances .NET 10
    /// also lists have no gateway of their own. "Internet" asks Windows itself
    /// (<c>INetworkListManager.GetConnectivity</c>), the same check that colors the taskbar icon.
    /// </remarks>
    public static NetworkState Network()
    {
        NetworkInterface? active = null;

        try
        {
            active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)));
        }
        catch (Exception)
        {
            // Without an adapter list it stays "disconnected".
        }

        var internet = InternetReachable();

        if (active is null) return new NetworkState("disconnected", false, false);

        var wireless = active.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        var name = wireless ? WlanProfile() is { Length: > 0 } ssid ? $"Wi-Fi · {ssid}" : "Wi-Fi" : "Ethernet";

        return new NetworkState(internet ? name : $"{name} · no internet", true, internet);
    }

    private static bool InternetReachable()
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return NetworkInterface.GetIsNetworkAvailable();

            instance = Activator.CreateInstance(type);
            if (instance is not INetworkListManager manager) return NetworkInterface.GetIsNetworkAvailable();

            // NLM_CONNECTIVITY_IPV4_INTERNET | NLM_CONNECTIVITY_IPV6_INTERNET
            return (manager.GetConnectivity() & (0x40 | 0x400)) != 0;
        }
        catch (Exception)
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        finally
        {
            if (instance is not null) Marshal.ReleaseComObject(instance);
        }
    }

    /// <summary>Name of the connected Wi-Fi profile.</summary>
    /// <remarks>
    /// <c>WLAN_CONNECTION_ATTRIBUTES</c> starts with two DWORDs; the profile name follows at offset 8 as
    /// a null-terminated string. Nothing else is read from the struct.
    /// </remarks>
    private static unsafe string? WlanProfile()
    {
        if (WlanOpenHandle(2, 0, out _, out var client) != 0) return null;

        nint list = 0;
        try
        {
            if (WlanEnumInterfaces(client, 0, out list) != 0 || list == 0) return null;

            var count = *(uint*)list;

            // WLAN_INTERFACE_INFO_LIST: two DWORDs header, then per entry a GUID, 256 chars of
            // description and a DWORD state, 532 bytes.
            for (var i = 0; i < count; i++)
            {
                var entry = (byte*)list + 8 + i * 532;
                var guid = *(Guid*)entry;

                if (WlanQueryInterface(client, &guid, 7 /* wlan_intf_opcode_current_connection */, 0,
                        out _, out var data, out _) != 0 || data == 0)
                {
                    continue;
                }

                try
                {
                    var name = new string((char*)((byte*)data + 8));
                    if (name.Length > 0) return name;
                }
                finally
                {
                    WlanFreeMemory(data);
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (list != 0) WlanFreeMemory(list);
            WlanCloseHandle(client, 0);
        }
    }

    // ------------------------------------------------------------------ Do not disturb

    /// <summary>Whether Do Not Disturb is on: read, not switched.</summary>
    /// <remarks>
    /// <para>
    /// <b>Read only</b> because Windows has no API for it. Switching means writing an undocumented
    /// binary blob into the CloudStore (at two places whose path changed between Windows versions) and
    /// restarting the user's notification service. A mistake there scrambles the user's notifications.
    /// The control center shows the state and opens the real switch.
    /// </para>
    /// <para>
    /// Only the plain-text profile name in the blob is read; "Microsoft.QuietHoursProfile.Unrestricted"
    /// means off. If the format changes, <c>null</c> comes back and the tile says "unknown".
    /// </para>
    /// </remarks>
    public static bool? DoNotDisturb()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store";

        string[] candidates =
        [
            @"Cache\DefaultAccount\$$windows.data.notifications.quiethourssettings\Current",
        ];

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(store);
            if (root is null) return null;

            // The newer location carries a device GUID in its name and has to be searched.
            var current = root.OpenSubKey(@"DefaultAccount\Current");
            var newer = current?.GetSubKeyNames()
                .FirstOrDefault(n => n.EndsWith("$windows.data.donotdisturb.quiethourssettings", StringComparison.OrdinalIgnoreCase));
            current?.Dispose();

            if (newer is not null)
                candidates = [$@"DefaultAccount\Current\{newer}\windows.data.donotdisturb.quiethourssettings", .. candidates];

            foreach (var path in candidates)
            {
                using var key = root.OpenSubKey(path);
                if (key?.GetValue("Data") is not byte[] data) continue;

                var text = Encoding.Unicode.GetString(data);
                var start = text.IndexOf("Microsoft.QuietHoursProfile.", StringComparison.Ordinal);
                if (start < 0) continue;

                var profile = new string([.. text[(start + "Microsoft.QuietHoursProfile.".Length)..].TakeWhile(char.IsLetter)]);
                if (profile.Length == 0) continue;

                return !string.Equals(profile, "Unrestricted", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception)
        {
            // Unknown is an answer.
        }

        return null;
    }

    // ------------------------------------------------------------------ Dark mode

    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>Whether Windows and apps are dark. A missing value means light.</summary>
    public static bool DarkMode
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(Personalize);
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Switches Windows and apps between light and dark.</summary>
    /// <remarks>
    /// The same two values the Settings app writes, then the <c>ImmersiveColorSet</c> broadcast so the
    /// taskbar and running apps follow. Sent with a timeout so a hung window delays nothing.
    /// </remarks>
    public static string? SetDarkMode(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(Personalize, writable: true);
            key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        _ = Task.Run(() => SendMessageTimeoutW(0xFFFF, 0x001A, 0, "ImmersiveColorSet", 0x0002, 1000, out _));
        return null;
    }

    // ------------------------------------------------------------------ Recycle bin, lock, battery

    /// <summary>Empties the recycle bin. Windows asks first, with the item count.</summary>
    /// <remarks>
    /// The size is not queried beforehand: SHQueryRecycleBin took 30 s for 8190 items here.
    /// </remarks>
    public static void EmptyRecycleBin(nint owner) => SHEmptyRecycleBinW(owner, null, 0);

    public static void Lock() => LockWorkStation();

    public static BatteryState Battery()
    {
        if (!GetSystemPowerStatus(out var status)) return new BatteryState(false, 0, false);

        // 128 means "no battery", 255 "unknown".
        var present = status.BatteryFlag is not (128 or 255) && status.BatteryLifePercent <= 100;
        return new BatteryState(present, status.BatteryLifePercent, status.AcLineStatus == 1);
    }

    // ------------------------------------------------------------------ Win32

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid id);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnection(Guid id);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint version, nint reserved, out uint negotiated, out nint client);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint client, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint client, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static unsafe partial uint WlanQueryInterface(nint client, Guid* interfaceGuid, int opcode, nint reserved,
        out uint size, out nint data, out int valueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, string lParam,
        uint flags, uint timeout, out nint result);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(nint owner, string? root, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LockWorkStation();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);
}
