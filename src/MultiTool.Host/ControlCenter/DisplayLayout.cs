using System.Runtime.InteropServices;

namespace MultiTool.Host.ControlCenter;

/// <summary>One display in the arrangement, in screen pixels.</summary>
/// <param name="Position">"Left", "Top right", "Middle", or empty with a single display.</param>
/// <param name="Model">The monitor's own name from its EDID, e.g. "DELL U2720Q"; empty if it reports none.</param>
public sealed record DisplaySpot(nint Monitor, int X, int Y, int Width, int Height, bool IsPrimary, string Position, string Model);

/// <summary>Where each display sits and what it is called, so five sliders can be told apart.</summary>
/// <remarks>
/// <para>
/// <b>Position from the arrangement in Windows' display settings</b>, not from numbers Windows assigns: displays
/// are grouped into columns and rows by their centers, then named left, middle, right and top, bottom; four
/// or more in a row are counted from the left or top. "Display 2" says nothing about which screen it is.
/// </para>
/// <para>
/// <b>The model name from the EDID</b> through the display configuration API: dxva2 reports "Generic PnP
/// Monitor" for most screens.
/// </para>
/// </remarks>
internal static unsafe partial class DisplayLayout
{
    public static List<DisplaySpot> Read()
    {
        var monitors = new List<nint>();
        var state = GCHandle.Alloc(monitors);
        try
        {
            EnumDisplayMonitors(0, 0, &Visit, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        var names = ModelNames();
        var raw = new List<(nint Monitor, int X, int Y, int W, int H, bool Primary, string Model)>();

        foreach (var monitor in monitors)
        {
            var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
            if (!GetMonitorInfoW(monitor, &info)) continue;

            var device = new string(info.Device).TrimEnd('\0');
            raw.Add((monitor, info.Left, info.Top, info.Right - info.Left, info.Bottom - info.Top,
                (info.Flags & 1) != 0, names.TryGetValue(device, out var model) ? model : ""));
        }

        var columns = Bands(raw.Select(r => (Start: r.X, Size: r.W)).ToList());
        var rows = Bands(raw.Select(r => (Start: r.Y, Size: r.H)).ToList());
        var columnCount = columns.Distinct().Count();
        var rowCount = rows.Distinct().Count();

        return [.. raw.Select((r, i) => new DisplaySpot(r.Monitor, r.X, r.Y, r.W, r.H, r.Primary,
            Name(rows[i], rowCount, columns[i], columnCount), r.Model))];
    }

    /// <summary>Groups displays whose centers lie within half a display of each other; returns each one's band index.</summary>
    private static int[] Bands(List<(int Start, int Size)> spans)
    {
        var order = spans.Select((s, i) => (Center: s.Start + s.Size / 2.0, s.Size, Index: i)).OrderBy(s => s.Center).ToList();
        var bands = new int[spans.Count];
        var band = -1;
        var last = double.NegativeInfinity;

        foreach (var (center, size, index) in order)
        {
            if (center - last > size / 2.0) band++;
            bands[index] = band;
            last = center;
        }

        return bands;
    }

    private static string Name(int row, int rows, int column, int columns)
    {
        var horizontal = Place(column, columns, "left", "middle", "right", "from the left");
        var verticalName = Place(row, rows, "top", "middle", "bottom", "from the top");

        var text = (verticalName, horizontal) switch
        {
            ("", "") => "",
            ("", var h) => h,
            (var v, "") => v,
            ("middle", "middle") => "middle",
            (var v, var h) => $"{v} {h}",
        };

        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Place(int index, int count, string first, string middle, string last, string counted) => count switch
    {
        1 => "",
        2 => index == 0 ? first : last,
        3 => index == 0 ? first : index == 1 ? middle : last,
        _ => index == 0 ? first : index == count - 1 ? last : $"{Ordinal(index + 1)} {counted}",
    };

    private static string Ordinal(int n) => n switch
    {
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };

    /// <summary>GDI device name ("\\.\DISPLAY1") → the monitor's EDID name, for the active paths.</summary>
    private static Dictionary<string, string> ModelNames()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0 || pathCount == 0)
            return result;

        var paths = new byte[pathCount * PathInfoSize];
        var modes = new byte[modeCount * ModeInfoSize];

        fixed (byte* p = paths)
        fixed (byte* m = modes)
        {
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, 0) != 0) return result;

            var source = stackalloc byte[SourceNameSize];
            var target = stackalloc byte[TargetNameSize];

            for (var i = 0; i < pathCount; i++)
            {
                var path = p + i * PathInfoSize;

                new Span<byte>(source, SourceNameSize).Clear();
                *(int*)source = 1;                                  // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME
                *(int*)(source + 4) = SourceNameSize;
                *(long*)(source + 8) = *(long*)path;                // source adapter LUID
                *(uint*)(source + 16) = *(uint*)(path + 8);         // source id
                if (DisplayConfigGetDeviceInfo(source) != 0) continue;
                var gdi = new string((char*)(source + 20)).TrimEnd('\0');

                new Span<byte>(target, TargetNameSize).Clear();
                *(int*)target = 2;                                  // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
                *(int*)(target + 4) = TargetNameSize;
                *(long*)(target + 8) = *(long*)(path + 20);         // target adapter LUID
                *(uint*)(target + 16) = *(uint*)(path + 28);        // target id
                if (DisplayConfigGetDeviceInfo(target) != 0) continue;
                var model = new string((char*)(target + 36)).TrimEnd('\0');

                if (gdi.Length > 0 && model.Length > 0) result[gdi] = model;
            }
        }

        return result;
    }

    private const uint QdcOnlyActivePaths = 2;

    /// <summary>DISPLAYCONFIG_PATH_INFO: source info 20 bytes, target info 48, flags 4.</summary>
    private const int PathInfoSize = 72;

    private const int ModeInfoSize = 64;

    /// <summary>Header (20) and a 32-character GDI device name.</summary>
    private const int SourceNameSize = 84;

    /// <summary>Header (20), flags, output technology, EDID ids, connector, 64-character name, 128-character path.</summary>
    private const int TargetNameSize = 420;

    [UnmanagedCallersOnly]
    private static int Visit(nint monitor, nint dc, nint clip, nint state)
    {
        try
        {
            ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(monitor);
        }
        catch (Exception)
        {
        }

        return 1;
    }

    /// <summary>MONITORINFOEXW: size, monitor and work rectangles, flags, 32-character device name.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        public fixed char Device[32];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged<nint, nint, nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, MonitorInfoEx* info);

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount, byte* paths, ref uint modeCount, byte* modes, nint topology);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(byte* request);
}
