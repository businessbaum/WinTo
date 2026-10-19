using System.Runtime.InteropServices;
using System.Text;

namespace MultiTool.Host.ControlCenter;

/// <summary>A Wi-Fi network in range.</summary>
/// <param name="Signal">0…100.</param>
/// <param name="Profile">Saved profile name; <c>null</c> means Windows has to ask for the password.</param>
public sealed record WifiNetwork(string Ssid, int Signal, bool Secured, bool Connected, string? Profile);

/// <summary>Wi-Fi networks via the Native Wifi API (<c>wlanapi.dll</c>).</summary>
/// <remarks>
/// <para>
/// Connecting only works for networks with a saved profile; anything new needs a password, and Windows'
/// own flyout (<c>ms-availablenetworks:</c>) is the right place to type it.
/// </para>
/// <para>Struct offsets are for x64 and noted at each read; every call blocks briefly.</para>
/// </remarks>
internal static unsafe partial class Wlan
{
    private const int AvailableNetworkSize = 628;
    private const uint FlagConnected = 0x1;
    private const uint FlagHasProfile = 0x2;

    /// <summary>Starts a scan; results arrive within a few seconds.</summary>
    public static void Scan()
    {
        WithFirstInterface((client, iface) =>
        {
            _ = WlanScan(client, &iface, 0, 0, 0);
            return 0;
        });
    }

    /// <summary>Networks in range, one entry per name, connected first, then by signal.</summary>
    public static List<WifiNetwork> Networks() => WithFirstInterface((client, iface) =>
    {
        var result = new Dictionary<string, WifiNetwork>(StringComparer.Ordinal);

        // WLAN_AVAILABLE_NETWORK_INCLUDE_ALL_MANUAL_HIDDEN_PROFILES
        if (WlanGetAvailableNetworkList(client, &iface, 0x2, 0, out var list) != 0 || list == 0)
            return [];

        try
        {
            var count = *(uint*)list;
            for (var i = 0; i < count; i++)
            {
                var entry = (byte*)list + 8 + i * AvailableNetworkSize;

                var profile = new string((char*)entry);              // WCHAR[256] at 0
                var length = Math.Min(*(uint*)(entry + 512), 32u);    // DOT11_SSID.uSSIDLength
                var ssid = Encoding.UTF8.GetString(entry + 516, (int)length);
                if (ssid.Length == 0) continue;

                var signal = (int)*(uint*)(entry + 604);              // wlanSignalQuality
                var secured = *(int*)(entry + 608) != 0;              // bSecurityEnabled
                var flags = *(uint*)(entry + 620);                    // dwFlags

                var network = new WifiNetwork(ssid, signal, secured,
                    (flags & FlagConnected) != 0,
                    (flags & FlagHasProfile) != 0 && profile.Length > 0 ? profile : null);

                // The same network shows up once with and once without profile.
                if (!result.TryGetValue(ssid, out var known)
                    || (network.Connected && !known.Connected)
                    || (network.Profile is not null && known.Profile is null && !known.Connected))
                {
                    result[ssid] = network with { Signal = Math.Max(network.Signal, known?.Signal ?? 0) };
                }
            }
        }
        finally
        {
            WlanFreeMemory(list);
        }

        return result.Values
            .OrderByDescending(n => n.Connected)
            .ThenByDescending(n => n.Signal)
            .ToList();
    }) ?? [];

    /// <summary>Saved networks, readable without location access.</summary>
    /// <remarks>
    /// Since Windows 11 24H2 scan results need location access; with it off,
    /// <see cref="Networks"/> stays empty while saved profiles can still be listed and connected.
    /// </remarks>
    public static List<string> SavedProfiles() => WithFirstInterface((client, iface) =>
    {
        if (WlanGetProfileList(client, &iface, 0, out var list) != 0 || list == 0) return [];
        try
        {
            // WLAN_PROFILE_INFO_LIST: two DWORDs, then per entry WCHAR[256] and a DWORD, 516 bytes.
            var count = *(uint*)list;
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
            {
                var name = new string((char*)((byte*)list + 8 + i * 516));
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }) ?? [];

    /// <summary>Connects with the saved profile. <c>null</c> on success, otherwise the reason.</summary>
    public static string? Connect(string profile) => WithFirstInterface<string?>((client, iface) =>
    {
        fixed (char* name = profile)
        {
            var parameters = new ConnectionParameters
            {
                Mode = 0,           // wlan_connection_mode_profile
                Profile = name,
                BssType = 1,        // dot11_BSS_type_infrastructure
            };

            var error = WlanConnect(client, &iface, &parameters, 0);
            return error == 0 ? null : $"Connecting failed (error {error}).";
        }
    }) ?? "No Wi-Fi adapter.";

    public static string? Disconnect() => WithFirstInterface<string?>((client, iface) =>
    {
        var error = WlanDisconnect(client, &iface, 0);
        return error == 0 ? null : $"Disconnecting failed (error {error}).";
    }) ?? "No Wi-Fi adapter.";

    /// <summary>Runs on the first Wi-Fi interface; <c>default</c> without one.</summary>
    private static T? WithFirstInterface<T>(Func<nint, Guid, T> action)
    {
        if (WlanOpenHandle(2, 0, out _, out var client) != 0) return default;

        nint list = 0;
        try
        {
            if (WlanEnumInterfaces(client, 0, out list) != 0 || list == 0) return default;
            if (*(uint*)list == 0) return default;

            // WLAN_INTERFACE_INFO_LIST: two DWORDs, then entries starting with the interface GUID.
            var iface = *(Guid*)((byte*)list + 8);
            return action(client, iface);
        }
        catch (Exception)
        {
            return default;
        }
        finally
        {
            if (list != 0) WlanFreeMemory(list);
            WlanCloseHandle(client, 0);
        }
    }

    /// <summary>WLAN_CONNECTION_PARAMETERS, 40 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionParameters
    {
        public int Mode;
        public char* Profile;
        public nint Ssid;
        public nint BssidList;
        public int BssType;
        public uint Flags;
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiatedVersion, out nint client);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint client, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint client, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanScan(nint client, Guid* iface, nint ssid, nint ie, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanGetAvailableNetworkList(nint client, Guid* iface, uint flags, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanGetProfileList(nint client, Guid* iface, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanConnect(nint client, Guid* iface, ConnectionParameters* parameters, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanDisconnect(nint client, Guid* iface, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
