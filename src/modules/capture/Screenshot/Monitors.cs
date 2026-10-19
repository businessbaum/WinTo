using System.Runtime.InteropServices;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>A display, in screen pixels.</summary>
public sealed record Monitor(string Name, ScreenRect Bounds, bool IsPrimary, double Scaling)
{
    public string Label => $"{Name} · {Bounds.Width}×{Bounds.Height}{(IsPrimary ? " (primary)" : "")}";
}

/// <summary>The displays, straight from Windows.</summary>
/// <remarks>
/// Not Avalonia's <c>Screens</c>: those belong to a <c>TopLevel</c>, known only to a view inside a window.
/// The hotkey also fires while this module's tab is hidden and its view detached.
/// </remarks>
internal static unsafe partial class Monitors
{
    private const uint MonitorinfoPrimary = 1;

    public static List<Monitor> All()
    {
        var found = new List<nint>();
        var state = GCHandle.Alloc(found);

        try
        {
            EnumDisplayMonitors(0, 0, &Visit, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        var result = new List<Monitor>(found.Count);
        var index = 0;

        foreach (var handle in found)
        {
            var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
            if (!GetMonitorInfoW(handle, &info)) continue;

            index++;

            var scaling = 1.0;
            if (GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 && dpiX > 0) scaling = dpiX / 96.0;

            var bounds = new ScreenRect(
                info.Monitor.Left, info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);

            result.Add(new Monitor($"Display {index}", bounds, (info.Flags & MonitorinfoPrimary) != 0, scaling));
        }

        // The primary display first; it's the one meant when nothing is said.
        return [.. result.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X)];
    }

    /// <summary>The display under the mouse pointer.</summary>
    public static Monitor? UnderCursor()
    {
        var (x, y) = ScreenCapture.Cursor();
        var all = All();
        return all.FirstOrDefault(m => m.Bounds.Contains(x, y)) ?? all.FirstOrDefault();
    }

    [UnmanagedCallersOnly]
    private static int Visit(nint monitor, nint dc, Rect* clip, nint state)
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, MonitorInfo* info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
