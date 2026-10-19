using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using MultiTool.Abstractions;
using NAudio.CoreAudioApi;

namespace MultiTool.Modules.SystemOverview;

/// <param name="Name">"Speakers (Realtek High Definition Audio)".</param>
/// <param name="Volume">0…1, or <c>null</c> if the device reports no volume.</param>
public sealed record AudioEndpoint(string Id, string Name, bool IsDefault, double? Volume, bool Muted);

public sealed record AudioSnapshot(
    IReadOnlyList<AudioEndpoint> Playback,
    IReadOnlyList<AudioEndpoint> Recording)
{
    public static readonly AudioSnapshot Empty = new([], []);

    public string DefaultPlaybackName => Playback.FirstOrDefault(e => e.IsDefault)?.Name ?? "none";

    public string DefaultRecordingName => Recording.FirstOrDefault(e => e.IsDefault)?.Name ?? "none";
}

public sealed record NetworkAdapter(
    string Name,
    string Description,
    string Kind,
    string Addresses,
    string Gateway,
    string Dns,
    string Speed,
    string MacAddress,
    long BytesSent,
    long BytesReceived,
    bool IsUp,
    bool IsEnabled)
{
    public string StateText => !IsEnabled ? "disabled"
        : IsUp ? "connected"
        : "no signal";

    /// <summary>The adapter carrying the traffic, recognized by its default gateway.</summary>
    public bool IsDefaultRoute => IsUp && Gateway.Length > 0;
}

public sealed record DiskVolume(
    string Name,
    string Label,
    string FileSystem,
    long UsedBytes,
    long TotalBytes)
{
    public double Load => TotalBytes == 0 ? 0 : (double)UsedBytes / TotalBytes;

    public string UsageText => $"{Humanize.Bytes(UsedBytes)} of {Humanize.Bytes(TotalBytes)} used";
}

/// <summary>Queries devices and connections. <b>Everything here belongs on a background thread.</b></summary>
/// <remarks>
/// The soundboard once enumerated all audio devices in its view model constructor and took 487 ms,
/// half a second of a tile looking pressed. Core Audio is COM with several cross-apartment calls per
/// endpoint; no cache changes that. So querying and displaying are separate.
/// </remarks>
internal static class Devices
{
    public static AudioSnapshot ReadAudio()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            return new AudioSnapshot(
                ReadFlow(enumerator, DataFlow.Render),
                ReadFlow(enumerator, DataFlow.Capture));
        }
        catch (Exception)
        {
            // Without an audio driver there are no devices. An answer, not an error.
            return AudioSnapshot.Empty;
        }
    }

    private static List<AudioEndpoint> ReadFlow(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        var result = new List<AudioEndpoint>();

        string? defaultId = null;
        try
        {
            // Multimedia, not Console: the role Windows uses for music and video, the one switched in the
            // volume flyout.
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            defaultId = device.ID;
        }
        catch (Exception)
        {
            // No default device for this direction, e.g. a computer without a microphone.
        }

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                double? volume = null;
                var muted = false;

                try
                {
                    volume = device.AudioEndpointVolume.MasterVolumeLevelScalar;
                    muted = device.AudioEndpointVolume.Mute;
                }
                catch (Exception)
                {
                    // Some virtual devices (VB-CABLE for example) have no volume control; the row stays without a number.
                }

                result.Add(new AudioEndpoint(device.ID, device.FriendlyName, device.ID == defaultId, volume, muted));
            }
        }

        return [.. result.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Name, StringComparer.CurrentCulture)];
    }

    /// <summary>The registry branch holding <b>network connections</b>, what <c>ncpa.cpl</c> shows.</summary>
    private const string ConnectionRoot =
        @"SYSTEM\CurrentControlSet\Control\Network\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Is this a connection, or just a filter driver attached to one?</summary>
    /// <remarks>
    /// <para>
    /// Under .NET 10, <see cref="NetworkInterface.GetAllNetworkInterfaces"/> returned <b>50</b> entries here
    /// where Control Panel shows <b>4</b>. The rest are NDIS filter instances ("Ethernet-QoS Packet
    /// Scheduler-0000", "…-Npcap Packet Driver (NPCAP)-0000"…), one per installed filter and adapter.
    /// </para>
    /// <para>
    /// Not just cluttered but wrong: <b>a filter instance mirrors the byte counters of its adapter.</b> The
    /// Ethernet counter appeared six times and the throughput was six times too high (19.8 instead of
    /// 3.3 GB).
    /// </para>
    /// <para>
    /// A real connection has a <c>Connection</c> key under this branch, a filter instance doesn't. The same
    /// list the "Network Connections" folder uses, one registry access per adapter.
    /// </para>
    /// </remarks>
    private static bool IsRealConnection(NetworkInterface nic)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{ConnectionRoot}\{nic.Id}\Connection");
            return key is not null;
        }
        catch (Exception)
        {
            // No answer: rather show than swallow. One adapter too many is annoying, a missing one costs a search.
            return true;
        }
    }

    /// <summary>All connections, including disabled ones.</summary>
    /// <param name="connections">
    /// What <c>netsh</c> knows: connection name to admin state. The key set says which adapters Windows
    /// treats as connections, the values whether they're enabled. Empty or <c>null</c> means "not queried";
    /// then the registry filters and every adapter counts as enabled.
    /// </param>
    /// <remarks>
    /// <para>
    /// Before, only connected adapters were listed, missing exactly what people look for here: the one
    /// that doesn't work. .NET doesn't distinguish "disabled" from "cable unplugged" (both
    /// <see cref="OperationalStatus.Down"/>); netsh does.
    /// </para>
    /// <para>
    /// <b>Two filters.</b> The registry removes filter drivers, which alone fixes the list. netsh goes further
    /// and knows exactly the entries of the "Network Connections" folder, without WAN miniports and
    /// Wi-Fi Direct adapters. If netsh answers, its list applies; the per-second throughput uses the
    /// registry, since a process start per second would be excessive.
    /// </para>
    /// </remarks>
    public static List<NetworkAdapter> ReadNetwork(IReadOnlyDictionary<string, bool>? connections = null)
    {
        var result = new List<NetworkAdapter>();
        var known = connections is { Count: > 0 } ? connections : null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            if (known is not null ? !known.ContainsKey(nic.Name) : !IsRealConnection(nic)) continue;

            IPInterfaceProperties properties;
            try
            {
                properties = nic.GetIPProperties();
            }
            catch (Exception)
            {
                // A disabled adapter has no IP properties. It belongs in the list anyway, especially that one.
                result.Add(new NetworkAdapter(
                    nic.Name, nic.Description, KindOf(nic.NetworkInterfaceType),
                    "", "", "", "", FormatMac(nic.GetPhysicalAddress()), 0, 0,
                    IsUp: false,
                    IsEnabled: known is null
                        || !known.TryGetValue(nic.Name, out var stillEnabled)
                        || stillEnabled));
                continue;
            }

            var addresses = properties.UnicastAddresses
                .Where(a => a.Address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork
                         || !a.Address.IsIPv6LinkLocal)
                .Select(a => a.Address.ToString());

            var gateway = properties.GatewayAddresses
                .Select(g => g.Address)
                .Where(a => !IPAddress.Any.Equals(a))
                .Select(a => a.ToString());

            var dns = properties.DnsAddresses.Select(a => a.ToString());

            long sent = 0, received = 0;
            try
            {
                var statistics = nic.GetIPStatistics();
                sent = statistics.BytesSent;
                received = statistics.BytesReceived;
            }
            catch (Exception)
            {
                // Some virtual adapters keep no counters.
            }

            result.Add(new NetworkAdapter(
                Name: nic.Name,
                Description: nic.Description,
                Kind: KindOf(nic.NetworkInterfaceType),
                Addresses: string.Join("   ", addresses),
                Gateway: string.Join("   ", gateway),
                Dns: string.Join("   ", dns),
                Speed: nic.Speed > 0 ? $"{nic.Speed / 1_000_000} Mbit/s" : "",
                MacAddress: FormatMac(nic.GetPhysicalAddress()),
                BytesSent: sent,
                BytesReceived: received,
                IsUp: nic.OperationalStatus == OperationalStatus.Up,
                IsEnabled: known is null
                    || !known.TryGetValue(nic.Name, out var enabled)
                    || enabled));
        }

        // Adapter with default gateway first (the one carrying traffic), disabled ones last.
        return [.. result
            .OrderByDescending(a => a.IsDefaultRoute)
            .ThenByDescending(a => a.IsEnabled)
            .ThenByDescending(a => a.IsUp)
            .ThenBy(a => a.Name, StringComparer.CurrentCulture)];
    }

    private static string KindOf(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx => "Cable",
        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
        NetworkInterfaceType.Ppp => "Dial-up",
        _ => type.ToString(),
    };

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    public static List<DiskVolume> ReadDisks()
    {
        var result = new List<DiskVolume>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                if (drive.TotalSize <= 0) continue;

                result.Add(new DiskVolume(
                    Name: drive.Name.TrimEnd('\\'),
                    Label: drive.VolumeLabel.Length > 0 ? drive.VolumeLabel : "no label",
                    FileSystem: drive.DriveFormat,
                    UsedBytes: drive.TotalSize - drive.TotalFreeSpace,
                    TotalBytes: drive.TotalSize));
            }
            catch (Exception)
            {
                // A drive disappearing between IsReady and the query (an unplugged stick) is everyday life.
            }
        }

        return result;
    }
}
