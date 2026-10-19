using System.Runtime.InteropServices;

namespace MultiTool.Host.ControlCenter;

/// <summary>A display whose brightness can be set over DDC/CI.</summary>
public sealed class DisplayBrightness(nint handle, string name, int minimum, int maximum, int current)
{
    internal nint Handle { get; } = handle;

    public string Name { get; } = name;

    /// <summary>Where it sits; <c>null</c> if the display wasn't found in the arrangement.</summary>
    public DisplaySpot? Spot { get; init; }

    /// <summary>Every display, for the small map on the row.</summary>
    public IReadOnlyList<DisplaySpot> Layout { get; init; } = [];

    public int Minimum { get; } = minimum;

    public int Maximum { get; } = maximum;

    public int Current { get; set; } = current;
}

/// <summary>Display brightness over DDC/CI, the line the monitor's own menu uses.</summary>
/// <remarks>
/// <para>
/// The documented Monitor Configuration API in <c>dxva2.dll</c>, no rights needed. Works for
/// external monitors with DDC/CI enabled; built-in laptop panels are not on DDC.
/// </para>
/// <para>
/// <b>Slow:</b> one call takes 40 to 100 ms and a second right behind it is sometimes dropped. A
/// slider setting every tick would queue dozens of calls, so <see cref="Set"/> writes only the
/// latest wish, on its own thread, one after another.
/// </para>
/// </remarks>
internal static unsafe partial class Brightness
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, int> Wanted = [];
    private static Thread? _writer;

    /// <summary>All displays answering DDC/CI. Slow; call on a background thread.</summary>
    /// <remarks>Handles stay open until <see cref="Release"/>.</remarks>
    public static List<DisplayBrightness> Read()
    {
        var layout = DisplayLayout.Read();
        var result = new List<DisplayBrightness>();
        var index = 0;

        foreach (var spot in layout)
        {
            var monitor = spot.Monitor;
            index++;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0) continue;

            var physical = new PhysicalMonitor[count];
            fixed (PhysicalMonitor* array = physical)
            {
                if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, array)) continue;
            }

            foreach (var item in physical)
            {
                var copy = item;
                var description = new string(copy.Description).TrimEnd('\0');

                if (GetMonitorBrightness(item.Handle, out var minimum, out var current, out var maximum) && maximum > minimum)
                {
                    var name = spot.Model is { Length: > 0 } ? spot.Model
                        : description is { Length: > 0 } && description != "Generic PnP Monitor" ? description
                        : $"Display {index}";

                    result.Add(new DisplayBrightness(item.Handle, name, (int)minimum, (int)maximum, (int)current)
                    {
                        Spot = spot,
                        Layout = layout,
                    });
                }
                else
                {
                    DestroyPhysicalMonitor(item.Handle);
                }
            }
        }

        return result;
    }

    /// <summary>Queues the wish; the writer thread writes it, the latest wins.</summary>
    public static void Set(DisplayBrightness display, int value)
    {
        lock (Gate)
        {
            Wanted[display.Handle] = Math.Clamp(value, display.Minimum, display.Maximum);

            if (_writer is { IsAlive: true }) return;

            _writer = new Thread(Write) { IsBackground = true, Name = "WinTo brightness" };
            _writer.Start();
        }
    }

    private static void Write()
    {
        while (true)
        {
            KeyValuePair<nint, int> next;
            lock (Gate)
            {
                if (Wanted.Count == 0)
                {
                    _writer = null;
                    return;
                }

                next = Wanted.First();
                Wanted.Remove(next.Key);
            }

            SetMonitorBrightness(next.Key, (uint)next.Value);
        }
    }

    /// <summary>Returns the handles. When the control center closes.</summary>
    public static void Release(IEnumerable<DisplayBrightness> displays)
    {
        var handles = displays.Select(d => d.Handle).ToList();

        // On a background thread: a write still running finishes before its handle goes away.
        _ = Task.Run(() =>
        {
            for (var i = 0; i < 50; i++)
            {
                lock (Gate)
                {
                    if (_writer is null && Wanted.Count == 0) break;
                }

                Thread.Sleep(20);
            }

            foreach (var handle in handles) DestroyPhysicalMonitor(handle);
        });
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public nint Handle;
        public fixed char Description[128];
    }

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, PhysicalMonitor* monitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorBrightness(nint monitor, out uint minimum, out uint current, out uint maximum);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMonitorBrightness(nint monitor, uint value);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyPhysicalMonitor(nint monitor);
}
