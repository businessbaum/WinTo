using System.Runtime.InteropServices;
using Avalonia.Threading;
using Microsoft.Win32;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>Puts windows back where they were after sleep, or when a display comes back.</summary>
/// <remarks>
/// <para>
/// <b>The problem:</b> a display that sleeps or is unplugged disappears for Windows, which moves its windows to
/// another one and doesn't move them back. Docking a laptop or waking a DisplayPort monitor ends with every
/// window on one screen.
/// </para>
/// <para>
/// <b>Positions per display arrangement.</b> Every 45 seconds the positions of the visible windows are taken,
/// stored under the current arrangement (the display rectangles). When the arrangement changes, or the
/// computer wakes, and a stored one matches again, windows that moved go back. Snapshots pause for a few
/// seconds around a change, or the shuffled state would overwrite the good one.
/// </para>
/// <para>
/// <b>By window handle, in memory.</b> A handle stays the same while the program runs, through sleep and
/// display changes; nothing is matched by title and nothing is written to disk. Minimized windows are left
/// alone. A window of a program running as administrator can't be moved from an unelevated WinTo.
/// </para>
/// </remarks>
public sealed unsafe partial class WindowKeeperFeature(ShellViewModel shell) : Feature(shell)
{
    private const string WindowsPath =
        "M3 5a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v2h-2V5H5v8h2v2H5a2 2 0 0 1-2-2V5Zm6 6a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-8a2 2 0 0 1-2-2v-8Zm2 0v8h8v-8h-8Z";

    private readonly Dictionary<string, List<Spot>> _layouts = [];
    private DispatcherTimer? _timer;
    private DateTime _quietUntil;
    private int _restored;

    public override string Id => "window-keeper";
    public override string Name => "Keep window positions";
    public override string Description =>
        "Windows go back where they were after sleep, or when a display is plugged in again.";
    public override string Group => "Windows";
    protected override string IconPath => WindowsPath;

    protected override bool ReadEnabled() => !Settings.Switches.TryGetValue(Id, out var on) || on;

    protected override void StoreEnabled(bool value) => Settings.Switches[Id] = value;

    protected override void Start()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _timer.Tick += (_, _) => Snapshot();
        _timer.Start();

        Snapshot();
    }

    protected override void Stop()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        _timer?.Stop();
        _timer = null;
        _layouts.Clear();
        Detail = "";
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        // Windows itself moves windows for a second or two after a change; restore after that.
        _quietUntil = DateTime.UtcNow.AddSeconds(8);
        DispatcherTimer.RunOnce(Restore, TimeSpan.FromSeconds(2.5));
        DispatcherTimer.RunOnce(Restore, TimeSpan.FromSeconds(6));
    });

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Snapshot();
                _quietUntil = DateTime.MaxValue;
                break;

            case PowerModes.Resume:
                // Displays wake one after another; two passes catch the late one.
                _quietUntil = DateTime.UtcNow.AddSeconds(12);
                DispatcherTimer.RunOnce(Restore, TimeSpan.FromSeconds(3));
                DispatcherTimer.RunOnce(Restore, TimeSpan.FromSeconds(9));
                break;
        }
    });

    private void Snapshot()
    {
        if (_timer is null || DateTime.UtcNow < _quietUntil) return;

        try
        {
            var spots = Capture();
            _layouts[LayoutKey()] = spots;
            Detail = _restored > 0
                ? $"{spots.Count} windows remembered · {_restored} put back since start"
                : $"{spots.Count} windows remembered on {(_layouts.Count == 1 ? "this display arrangement" : $"{_layouts.Count} display arrangements")}";
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("features").Warn($"Window positions: {ex.Message}");
        }
    }

    private void Restore()
    {
        if (_timer is null || !_layouts.TryGetValue(LayoutKey(), out var spots)) return;

        var moved = 0;
        foreach (var spot in spots)
        {
            try
            {
                if (Put(spot)) moved++;
            }
            catch (Exception)
            {
                // One window that refuses doesn't stop the others.
            }
        }

        if (moved == 0) return;

        _restored += moved;
        Detail = $"{moved} {(moved == 1 ? "window" : "windows")} put back at {DateTime.Now:t}";
    }

    // --------------------------------------------------------------- Windows

    private readonly record struct Spot(nint Handle, Rect Bounds, bool Maximized);

    /// <summary>Moves one window back if it moved. <c>true</c> if it did.</summary>
    private static bool Put(Spot spot)
    {
        if (!IsWindow(spot.Handle) || IsIconic(spot.Handle) || !IsWindowVisible(spot.Handle)) return false;
        if (!GetWindowRect(spot.Handle, out var now)) return false;

        var maximized = IsZoomed(spot.Handle);
        if (maximized == spot.Maximized && Near(now, spot.Bounds)) return false;

        const uint flags = 0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */ | 0x4000 /* ASYNCWINDOWPOS */;

        // A maximized window is restored, moved onto its display and maximized there again.
        if (maximized) ShowWindowAsync(spot.Handle, 4 /* SW_SHOWNOACTIVATE */);

        var b = spot.Bounds;
        SetWindowPos(spot.Handle, 0, b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top, flags);

        if (spot.Maximized) ShowWindowAsync(spot.Handle, 3 /* SW_MAXIMIZE */);
        return true;
    }

    private static bool Near(Rect a, Rect b) =>
        Math.Abs(a.Left - b.Left) <= 4 && Math.Abs(a.Top - b.Top) <= 4 &&
        Math.Abs(a.Right - b.Right) <= 4 && Math.Abs(a.Bottom - b.Bottom) <= 4;

    /// <summary>The visible top-level windows a user would call windows.</summary>
    private static List<Spot> Capture()
    {
        var handles = new List<nint>();
        var state = GCHandle.Alloc(handles);
        try
        {
            EnumWindows(&CollectWindow, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        var own = (uint)Environment.ProcessId;
        var spots = new List<Spot>();

        foreach (var handle in handles)
        {
            if (!IsWindowVisible(handle) || IsIconic(handle) || GetWindow(handle, 4 /* GW_OWNER */) != 0) continue;
            if ((GetWindowLongPtrW(handle, -20 /* GWL_EXSTYLE */) & 0x80 /* WS_EX_TOOLWINDOW */) != 0) continue;
            if (GetWindowTextLengthW(handle) == 0) continue;

            int cloaked;
            if (DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, &cloaked, sizeof(int)) == 0 && cloaked != 0) continue;

            _ = GetWindowThreadProcessId(handle, out var pid);
            if (pid == own) continue;

            if (IsShell(handle)) continue;
            if (!GetWindowRect(handle, out var rect)) continue;

            spots.Add(new Spot(handle, rect, IsZoomed(handle)));
        }

        return spots;
    }

    private static bool IsShell(nint handle)
    {
        var name = stackalloc char[64];
        var length = GetClassNameW(handle, name, 64);
        var cls = new string(name, 0, Math.Max(0, length));
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    [UnmanagedCallersOnly]
    private static int CollectWindow(nint handle, nint state)
    {
        ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(handle);
        return 1;
    }

    /// <summary>The display rectangles, e.g. "0,0,2560,1440|2560,0,4480,1080".</summary>
    private static string LayoutKey()
    {
        var rects = new List<Rect>();
        var state = GCHandle.Alloc(rects);
        try
        {
            EnumDisplayMonitors(0, 0, &CollectMonitor, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        return string.Join("|", rects.OrderBy(r => r.Left).ThenBy(r => r.Top).Select(r => $"{r.Left},{r.Top},{r.Right},{r.Bottom}"));
    }

    [UnmanagedCallersOnly]
    private static int CollectMonitor(nint monitor, nint dc, Rect* rect, nint state)
    {
        ((List<Rect>)GCHandle.FromIntPtr(state).Target!).Add(*rect);
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(int Left, int Top, int Right, int Bottom);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayMonitors(nint dc, nint clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint window, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(nint window, char* name, int length);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint window, int command);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);
}
