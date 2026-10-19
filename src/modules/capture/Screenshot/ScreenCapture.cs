using System.Runtime.InteropServices;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>A window that can be captured.</summary>
public sealed record CaptureWindow(nint Handle, string Title, string Process)
{
    public string Label => Process.Length > 0 ? $"{Title}  ·  {Process}" : Title;
}

/// <summary>Captures screens and windows via GDI, without a library.</summary>
/// <remarks>
/// <para>
/// <b>Pixels, not DIPs.</b> Avalonia registers as per-monitor DPI aware, so all coordinates here are real
/// screen pixels, also on a second display with different scaling.
/// </para>
/// <para>
/// <b>Screen via <c>BitBlt</c>, window via <c>PrintWindow</c>.</b> <c>BitBlt</c> copies what is visible and is
/// fast. <c>PrintWindow</c> with <c>PW_RENDERFULLCONTENT</c> asks the window for its content, so a window
/// comes out complete even when covered.
/// </para>
/// <para>
/// A game in exclusive fullscreen bypasses desktop composition and GDI often sees black. Borderless
/// window mode works.
/// </para>
/// </remarks>
internal static unsafe partial class ScreenCapture
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint PwRenderFullContent = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    /// <summary>The whole virtual desktop, all displays as they are arranged.</summary>
    public static (int X, int Y, int Width, int Height) VirtualScreen() =>
        (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    /// <summary>Copies a rectangle of the screen.</summary>
    public static CapturedImage Screen(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("Empty region.");

        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateDib(memory, width, height, out var bits);
        var previous = SelectObject(memory, bitmap);

        try
        {
            // CAPTUREBLT includes translucent windows such as menus and shadows; without it they'd be
            // missing although they were visible.
            if (!BitBlt(memory, 0, 0, width, height, screen, x, y, SrcCopy | CaptureBlt))
                throw new InvalidOperationException("The screen could not be copied.");

            return Copy(bits, width, height);
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Captures a window, even when covered.</summary>
    public static CapturedImage Window(nint handle)
    {
        if (IsIconic(handle)) throw new InvalidOperationException("The window is minimized.");
        if (!GetWindowRect(handle, out var outer)) throw new InvalidOperationException("The window no longer exists.");

        var width = outer.Right - outer.Left;
        var height = outer.Bottom - outer.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("The window has no area.");

        var screen = GetDC(0);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateDib(memory, width, height, out var bits);
        var previous = SelectObject(memory, bitmap);

        try
        {
            if (!PrintWindow(handle, memory, PwRenderFullContent))
                throw new InvalidOperationException("The window returned no image.");

            var image = Copy(bits, width, height);

            // Since Windows 10 every window has an invisible resize border of a few pixels; GetWindowRect
            // counts it and the image would get a black seam. DWM knows the visible bounds.
            Rect visible;
            if (DwmGetWindowAttribute(handle, DwmwaExtendedFrameBounds, &visible, sizeof(Rect)) == 0)
            {
                return image.Crop(visible.Left - outer.Left, visible.Top - outer.Top,
                    visible.Right - visible.Left, visible.Bottom - visible.Top) ?? image;
            }

            return image;
        }
        finally
        {
            SelectObject(memory, previous);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>Visible main windows with a title, for the "Window" choice.</summary>
    /// <param name="exceptProcess">This app itself isn't listed.</param>
    public static List<CaptureWindow> Windows(int exceptProcess)
    {
        var found = new List<(nint Handle, string Title, uint Pid)>();
        var state = GCHandle.Alloc(found);

        try
        {
            EnumWindows(&Visit, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        var names = new Dictionary<uint, string>();
        var result = new List<CaptureWindow>();

        foreach (var (handle, title, pid) in found)
        {
            if (pid == exceptProcess) continue;

            if (!names.TryGetValue(pid, out var name))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)pid);
                    name = process.ProcessName;
                }
                catch (Exception)
                {
                    name = "";
                }

                names[pid] = name;
            }

            result.Add(new CaptureWindow(handle, title, name));
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static int Visit(nint window, nint state)
    {
        try
        {
            if (IsWindowVisible(window) == 0 || GetWindow(window, 4) != 0 || IsIconic(window)) return 1;

            // Cloaked windows (suspended Store apps, windows on another virtual desktop) report as
            // visible but show nothing. DWM knows better.
            int cloaked;
            if (DwmGetWindowAttribute(window, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0) return 1;

            var length = GetWindowTextLengthW(window);
            if (length <= 0) return 1;

            var buffer = new char[length + 1];
            int read;
            fixed (char* text = buffer) read = GetWindowTextW(window, text, buffer.Length);
            if (read <= 0) return 1;

            GetWindowThreadProcessId(window, out var pid);

            var list = (List<(nint, string, uint)>)GCHandle.FromIntPtr(state).Target!;
            list.Add((window, new string(buffer, 0, read), pid));
        }
        catch (Exception)
        {
            // An exception must not cross back into user32.
        }

        return 1;
    }

    /// <summary>Where the mouse pointer is, in screen pixels.</summary>
    public static (int X, int Y) Cursor()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    private static nint CreateDib(nint dc, int width, int height, out byte* bits)
    {
        var info = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = width,

            // Negative means top-down; otherwise the image would be upside down.
            Height = -height,
            Planes = 1,
            BitCount = 32,
        };

        void* pointer;
        var bitmap = CreateDIBSection(dc, &info, 0, &pointer, 0, 0);
        if (bitmap == 0) throw new OutOfMemoryException("No memory for the image.");

        bits = (byte*)pointer;
        return bitmap;
    }

    private static CapturedImage Copy(byte* bits, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Marshal.Copy((nint)bits, pixels, 0, pixels.Length);

        // GDI leaves the alpha channel empty; saved as PNG the image would be transparent.
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        return new CapturedImage(pixels, width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
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
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, int operation);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint window, nint dc, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    private static partial int IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(nint window, char* text, int max);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial int EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint window, int attribute, void* value, int size);
}
