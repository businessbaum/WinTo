using System.Runtime.InteropServices;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>Puts an image on the Windows clipboard right away, as DIB and as PNG.</summary>
/// <remarks>
/// <para>
/// <b>Not through Avalonia's clipboard.</b> That one offers the bitmap lazily: Windows asks for the pixels
/// only when something is pasted. A capture from Win+Shift+S releases the module right after, the bitmap
/// goes with it, and the paste came out as a black image of the right size. Here the data is copied into
/// global memory before the call returns and belongs to Windows from then on.
/// </para>
/// <para>
/// <b>An owner window, briefly.</b> With a <c>null</c> owner, <c>EmptyClipboard</c> leaves the clipboard
/// without owner and <c>SetClipboardData</c> may fail. A message-only window owns it for the duration of
/// the call and is destroyed right after; data that isn't rendered lazily stays on the clipboard.
/// </para>
/// <para>
/// <b>Two formats.</b> <c>CF_DIB</c> is what every program reads; Windows derives <c>CF_BITMAP</c> and
/// <c>CF_DIBV5</c> from it. "PNG" is what browsers, Discord and Office take when it is there.
/// </para>
/// </remarks>
internal static unsafe partial class ClipboardImage
{
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const nint HwndMessage = -3;

    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? Set(CapturedImage image, byte[]? png)
    {
        var owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);

        try
        {
            // A clipboard manager or a remote session may hold the clipboard for a moment.
            var opened = false;
            for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(owner)); attempt++) Thread.Sleep(15);
            if (!opened) return "The clipboard is in use by another program.";

            try
            {
                if (!EmptyClipboard()) return "The clipboard could not be cleared.";
                if (!Put(CfDib, Dib(image))) return "The image could not be placed on the clipboard.";

                if (png is { Length: > 0 } && RegisterClipboardFormatW("PNG") is var format and not 0)
                    Put(format, png);

                return null;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (owner != 0) DestroyWindow(owner);
        }
    }

    /// <summary>Plain text, right away, for the quick text recognition and the color picker.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? SetText(string text)
    {
        var owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);

        try
        {
            var opened = false;
            for (var attempt = 0; attempt < 10 && !(opened = OpenClipboard(owner)); attempt++) Thread.Sleep(15);
            if (!opened) return "The clipboard is in use by another program.";

            try
            {
                if (!EmptyClipboard()) return "The clipboard could not be cleared.";

                // UTF-16 with the terminating zero, as CF_UNICODETEXT requires.
                var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
                return Put(CfUnicodeText, bytes) ? null : "The text could not be placed on the clipboard.";
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (owner != 0) DestroyWindow(owner);
        }
    }

    /// <summary>A packed DIB: header, then the rows bottom-up, 32 bits per pixel.</summary>
    private static byte[] Dib(CapturedImage image)
    {
        const int header = 40;
        var row = image.Width * 4;
        var data = new byte[header + row * image.Height];

        fixed (byte* d = data)
        {
            var h = (BitmapInfoHeader*)d;
            h->Size = header;
            h->Width = image.Width;
            h->Height = image.Height;
            h->Planes = 1;
            h->BitCount = 32;
            h->SizeImage = (uint)(row * image.Height);
        }

        for (var y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Pixels, y * row, data, header + (image.Height - 1 - y) * row, row);

        return data;
    }

    private static bool Put(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GmemMoveable, (nuint)data.Length);
        if (memory == 0) return false;

        var target = GlobalLock(memory);
        if (target == 0)
        {
            GlobalFree(memory);
            return false;
        }

        Marshal.Copy(data, 0, target, data.Length);
        GlobalUnlock(memory);

        // On success the memory belongs to Windows; only a failed call leaves it with the caller.
        if (SetClipboardData(format, memory) != 0) return true;

        GlobalFree(memory);
        return false;
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

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}
