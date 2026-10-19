using System.Runtime.InteropServices;

namespace MultiTool.Abstractions.Bluetooth;

/// <summary>What the Bluetooth device node knows about a paired device.</summary>
/// <param name="Name">The device name given at pairing.</param>
/// <param name="Battery">Charge in percent, only while connected.</param>
/// <param name="IsApple">Apple device (vendor id 0x004C in a service node).</param>
public sealed record BluetoothInfo(string Name, byte? Battery, bool IsApple);

/// <summary>Reads the <c>BTHENUM</c> device nodes via SetupAPI.</summary>
/// <remarks>
/// <para>
/// Two answers come from here: <b>which</b> audio endpoints are Bluetooth (via the shared container
/// id), and the <b>battery level</b>, reported over the hands-free link and stored by Windows as device
/// property <c>{104EA319-…} 2</c>, the number the Settings app shows. There is none while disconnected.
/// </para>
/// <para>
/// AirPods report <b>one</b> number this way, not left/right/case: those travel in Apple's encrypted BLE
/// advertisements.
/// </para>
/// </remarks>
public static class BluetoothDevices
{
    private static readonly DevPropKey KeyContainerId =
        new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    private static readonly DevPropKey KeyFriendlyName =
        new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary>Undocumented but stable since Windows 10: the Bluetooth battery level.</summary>
    private static readonly DevPropKey KeyBattery =
        new(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);

    /// <summary>Per container, what all its <c>BTHENUM</c> nodes know.</summary>
    public static Dictionary<Guid, BluetoothInfo> Snapshot()
    {
        var result = new Dictionary<Guid, BluetoothInfo>();

        var set = SetupDiGetClassDevsW(nint.Zero, "BTHENUM", nint.Zero,
            0x2 /* DIGCF_PRESENT */ | 0x4 /* DIGCF_ALLCLASSES */);
        if (set == new nint(-1)) return result;

        try
        {
            var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };

            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var container = ReadGuid(set, ref data, KeyContainerId);
                if (container is not { } id || id == Guid.Empty) continue;

                var instance = ReadInstanceId(set, ref data) ?? "";
                var battery = ReadByte(set, ref data, KeyBattery);

                // The main node is BTHENUM\DEV_<address> and carries the device name; service nodes carry
                // the vendor id.
                var isMain = instance.StartsWith(@"BTHENUM\DEV_", StringComparison.OrdinalIgnoreCase);
                var name = isMain ? ReadString(set, ref data, KeyFriendlyName) : null;
                var isApple = instance.Contains("VID&0001004C", StringComparison.OrdinalIgnoreCase);

                if (result.TryGetValue(id, out var known))
                {
                    result[id] = new BluetoothInfo(
                        name ?? known.Name,
                        battery ?? known.Battery,
                        isApple || known.IsApple);
                }
                else
                {
                    result[id] = new BluetoothInfo(name ?? "", battery, isApple);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static Guid? ReadGuid(nint set, ref SpDevInfoData data, DevPropKey key)
    {
        var buffer = new byte[16];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out var type,
                buffer, (uint)buffer.Length, out var required, 0)
            && type == 0x000D /* DEVPROP_TYPE_GUID */ && required == 16
                ? new Guid(buffer)
                : null;
    }

    private static byte? ReadByte(nint set, ref SpDevInfoData data, DevPropKey key)
    {
        var buffer = new byte[1];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out var type,
                buffer, (uint)buffer.Length, out _, 0)
            && type == 0x0003 /* DEVPROP_TYPE_BYTE */
                ? buffer[0]
                : null;
    }

    private static string? ReadString(nint set, ref SpDevInfoData data, DevPropKey key)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDevicePropertyW(set, ref data, ref key, out var type,
                buffer, (uint)buffer.Length, out var required, 0)
            || type != 0x0012 /* DEVPROP_TYPE_STRING */ || required < 2)
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)required);
        return text.TrimEnd('\0');
    }

    private static string? ReadInstanceId(nint set, ref SpDevInfoData data)
    {
        var buffer = new char[512];
        return SetupDiGetDeviceInstanceIdW(set, ref data, buffer, (uint)buffer.Length, out var required)
            ? new string(buffer, 0, Math.Max(0, (int)required - 1))
            : null;
    }

    // ------------------------------------------------------------------ P/Invoke

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(nint classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref SpDevInfoData data);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDevicePropertyW(nint set, ref SpDevInfoData data,
        ref DevPropKey key, out uint propertyType, byte[]? buffer, uint bufferSize,
        out uint requiredSize, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref SpDevInfoData data,
        char[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint set);
}
