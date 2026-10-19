using System.Runtime.InteropServices;

namespace MultiTool.Host.ControlCenter;

/// <summary>A paired Bluetooth device as Windows remembers it.</summary>
/// <param name="ClassOfDevice">Major class in bits 8–12: 4 audio, 5 peripheral (mouse, keyboard, controller).</param>
public sealed record PairedDevice(string Name, bool Connected, uint ClassOfDevice)
{
    public bool IsAudio => ((ClassOfDevice >> 8) & 0x1F) == 4;
}

/// <summary>All paired devices via <c>BluetoothFindFirstDevice</c>, without an inquiry (no radio scan).</summary>
internal static partial class BluetoothList
{
    /// <summary>BLUETOOTH_DEVICE_INFO: 560 bytes on x64, name at offset 64.</summary>
    private const int DeviceInfoSize = 560;

    public static unsafe List<PairedDevice> Paired()
    {
        var result = new List<PairedDevice>();

        // BLUETOOTH_DEVICE_SEARCH_PARAMS: dwSize, five BOOLs, a UCHAR timeout, then the radio handle.
        var search = stackalloc byte[40];
        new Span<byte>(search, 40).Clear();
        *(uint*)search = 40;
        *(int*)(search + 4) = 1;   // fReturnAuthenticated
        *(int*)(search + 8) = 1;   // fReturnRemembered
        *(int*)(search + 16) = 1;  // fReturnConnected

        var info = stackalloc byte[DeviceInfoSize];
        new Span<byte>(info, DeviceInfoSize).Clear();
        *(uint*)info = DeviceInfoSize;

        var find = BluetoothFindFirstDevice(search, info);
        if (find == 0) return result;

        try
        {
            do
            {
                var name = new string((char*)(info + 64));
                var connected = *(int*)(info + 20) != 0;
                var cod = *(uint*)(info + 16);
                if (name.Length > 0) result.Add(new PairedDevice(name, connected, cod));

                new Span<byte>(info, DeviceInfoSize).Clear();
                *(uint*)info = DeviceInfoSize;
            }
            while (BluetoothFindNextDevice(find, info));
        }
        finally
        {
            BluetoothFindDeviceClose(find);
        }

        return result
            .GroupBy(d => d.Name, StringComparer.CurrentCulture)
            .Select(g => g.OrderByDescending(d => d.Connected).First())
            .ToList();
    }

    [LibraryImport("bthprops.cpl")]
    private static unsafe partial nint BluetoothFindFirstDevice(byte* searchParams, byte* deviceInfo);

    [LibraryImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool BluetoothFindNextDevice(nint find, byte* deviceInfo);

    [LibraryImport("bthprops.cpl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BluetoothFindDeviceClose(nint find);
}
