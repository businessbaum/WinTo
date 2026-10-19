using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace MultiTool.Host.Services;

/// <summary>Windows 11 rounded corners for windows without decorations.</summary>
/// <remarks>
/// Windows rounds a window by itself only while it has a frame. With <c>WindowDecorations="None"</c>
/// the corners are square unless the window asks via <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>. Windows 10
/// ignores the attribute; maximized windows stay square either way.
/// </remarks>
internal static partial class WindowCorners
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    public static void Round(Window window) => window.Opened += (_, _) =>
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        if (window.TryGetPlatformHandle() is not { } handle) return;

        var preference = DwmwcpRound;
        _ = DwmSetWindowAttribute(handle.Handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
    };

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
}
