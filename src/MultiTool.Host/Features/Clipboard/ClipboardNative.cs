using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace MultiTool.Host.Features.Clipboard;

/// <summary>What was on the clipboard at one change, raw.</summary>
/// <param name="Png">Image bytes from the "PNG" format, if a program offered it.</param>
/// <param name="Dib">A packed DIB otherwise; converted to PNG off the listener thread.</param>
/// <param name="Source">Process name of the clipboard owner, for the list ("from chrome").</param>
/// <param name="Html">The "HTML Format" next to a text, as Windows defines it (header plus fragment).</param>
/// <param name="Rtf">"Rich Text Format" next to a text, raw.</param>
internal sealed record ClipboardSnapshot(
    string? Text, string[]? Files, byte[]? Png, byte[]? Dib, string? Source, string? Html = null, byte[]? Rtf = null);

/// <summary>Reads and writes the Windows clipboard through user32, for the history.</summary>
/// <remarks>
/// <para>
/// <b>Respects what other programs ask for.</b> Password managers mark their copies with
/// <c>ExcludeClipboardContentFromMonitorProcessing</c>, or <c>CanIncludeInClipboardHistory</c> = 0; the
/// password generator in Text tools does the same. Such a change is not read at all.
/// </para>
/// <para>
/// <b>One kind per entry.</b> Files win, then text, then an image: Excel puts both text and a picture of the
/// cells on the clipboard, and the text is what gets pasted again.
/// </para>
/// <para>
/// <b>Writes are immediate</b>, with a short-lived message-only window as owner, for the same reason as the
/// capture tool's <c>ClipboardImage</c>: nothing is rendered lazily from an object that may be gone by the
/// time something is pasted.
/// </para>
/// </remarks>
internal static unsafe partial class ClipboardNative
{
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint GmemMoveable = 0x0002;
    private const nint HwndMessage = -3;

    /// <summary>Larger texts are not kept: a log file copied whole is no history entry.</summary>
    private const int MaxTextChars = 1_000_000;

    /// <summary>Larger images are not kept; a 4K screenshot as DIB is about 33 MB.</summary>
    private const int MaxImageBytes = 80 * 1024 * 1024;

    /// <summary>Formatting larger than this is dropped and the text kept plain; a whole web page can be 10 MB.</summary>
    private const int MaxRichBytes = 4 * 1024 * 1024;

    private static readonly uint PngFormat = RegisterClipboardFormatW("PNG");
    private static readonly uint ExcludeFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint ViewerIgnoreFormat = RegisterClipboardFormatW("Clipboard Viewer Ignore");
    private static readonly uint DropEffectFormat = RegisterClipboardFormatW("Preferred DropEffect");
    private static readonly uint HtmlFormat = RegisterClipboardFormatW("HTML Format");
    private static readonly uint RtfFormat = RegisterClipboardFormatW("Rich Text Format");

    // --------------------------------------------------------------- Reading

    /// <summary>Reads the clipboard after a change. <c>null</c> if it is marked private, empty or unreadable.</summary>
    public static ClipboardSnapshot? Read(nint owner)
    {
        if (!Open(owner)) return null;

        try
        {
            if (Has(ExcludeFormat) || Has(ViewerIgnoreFormat)) return null;
            if (Has(HistoryFormat) && ReadInt(HistoryFormat) == 0) return null;

            var source = OwnerProcess();

            if (Has(CfHdrop) && ReadFiles() is { Length: > 0 } files)
                return new ClipboardSnapshot(null, files, null, null, source);

            if (Has(CfUnicodeText) && ReadText() is { } text && !string.IsNullOrWhiteSpace(text))
            {
                // Formatting rides along so a paste looks like the copy (Word, a web page, Outlook).
                var html = Has(HtmlFormat) && ReadBytes(HtmlFormat, MaxRichBytes) is { } h ? Encoding.UTF8.GetString(Trim(h)) : null;
                var rtf = Has(RtfFormat) && ReadBytes(RtfFormat, MaxRichBytes) is { } r ? Trim(r) : null;
                return new ClipboardSnapshot(text, null, null, null, source, html, rtf);
            }

            if (Has(PngFormat) && ReadBytes(PngFormat) is { } png)
                return new ClipboardSnapshot(null, null, png, null, source);

            if (Has(CfDib) && ReadBytes(CfDib) is { } dib)
                return new ClipboardSnapshot(null, null, null, dib, source);

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Has(uint format) => format != 0 && IsClipboardFormatAvailable(format);

    private static int? ReadInt(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == 0 || GlobalSize(handle) < 4) return null;

        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;

        try
        {
            return Marshal.ReadInt32(pointer);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadText()
    {
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == 0) return null;

        var chars = (int)Math.Min((long)GlobalSize(handle) / 2, int.MaxValue);
        if (chars > MaxTextChars) return null;

        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;

        try
        {
            var span = new ReadOnlySpan<char>((void*)pointer, chars);
            var end = span.IndexOf('\0');
            return new string(end >= 0 ? span[..end] : span);
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string[]? ReadFiles()
    {
        var drop = GetClipboardData(CfHdrop);
        if (drop == 0) return null;

        var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
        var files = new List<string>((int)Math.Min(count, 1000));

        for (uint i = 0; i < count && i < 1000; i++)
        {
            var length = DragQueryFileW(drop, i, null, 0);
            if (length == 0) continue;

            var buffer = new char[length + 1];
            fixed (char* b = buffer)
            {
                if (DragQueryFileW(drop, i, b, (uint)buffer.Length) > 0) files.Add(new string(b, 0, (int)length));
            }
        }

        return [.. files];
    }

    /// <summary>The bytes up to the first zero: the global block is often larger than its content.</summary>
    private static byte[] Trim(byte[] bytes) =>
        Array.IndexOf(bytes, (byte)0) is var end and >= 0 ? bytes[..end] : bytes;

    private static byte[]? ReadBytes(uint format, int max = MaxImageBytes)
    {
        var handle = GetClipboardData(format);
        if (handle == 0) return null;

        var size = GlobalSize(handle);
        if (size == 0 || size > (nuint)max) return null;

        var pointer = GlobalLock(handle);
        if (pointer == 0) return null;

        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? OwnerProcess()
    {
        var owner = GetClipboardOwner();
        if (owner == 0) return null;

        _ = GetWindowThreadProcessId(owner, out var pid);
        if (pid == 0) return null;

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // --------------------------------------------------------------- Images

    /// <summary>A packed DIB (24 or 32 bits, uncompressed) as PNG. <c>null</c> for anything else.</summary>
    /// <remarks>
    /// Most programs leave the fourth byte of a 32-bit DIB at 0; an image whose alpha is 0 everywhere is
    /// treated as opaque, or it would come out fully transparent.
    /// </remarks>
    public static (byte[] Png, int Width, int Height)? DibToPng(byte[] dib)
    {
        if (dib.Length < 40) return null;

        var headerSize = BitConverter.ToInt32(dib, 0);
        var width = BitConverter.ToInt32(dib, 4);
        var rawHeight = BitConverter.ToInt32(dib, 8);
        var bits = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        var colors = BitConverter.ToUInt32(dib, 32);

        if (bits is not (24 or 32) || compression is not (0 or 3) || width <= 0 || rawHeight == 0) return null;
        if (headerSize < 40 || colors > 256) return null;

        var height = Math.Abs(rawHeight);
        var topDown = rawHeight < 0;
        var offset = headerSize + (compression == 3 && headerSize == 40 ? 12 : 0) + (int)colors * 4;
        var stride = (width * bits + 31) / 32 * 4;
        if ((long)offset + (long)stride * height > dib.Length) return null;

        var pixels = new byte[width * height * 4];
        var anyAlpha = false;

        for (var y = 0; y < height; y++)
        {
            var source = offset + (topDown ? y : height - 1 - y) * stride;
            var target = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var s = source + x * (bits / 8);
                var t = target + x * 4;
                pixels[t] = dib[s];
                pixels[t + 1] = dib[s + 1];
                pixels[t + 2] = dib[s + 2];
                pixels[t + 3] = bits == 32 ? dib[s + 3] : (byte)255;
                if (bits == 32 && dib[s + 3] != 0) anyAlpha = true;
            }
        }

        if (bits == 32 && !anyAlpha)
        {
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, anyAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        fixed (byte* p = pixels)
        {
            using var image = SKImage.FromPixelCopy(info, (nint)p, width * 4);
            using var data = image?.Encode(SKEncodedImageFormat.Png, 100);
            return data is null ? null : (data.ToArray(), width, height);
        }
    }

    /// <summary>Width and height of a PNG, from its header.</summary>
    public static (int Width, int Height)? PngSize(byte[] png)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(png));
        return codec is null ? null : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>A PNG as a packed 32-bit DIB, bottom-up, for programs that don't read PNG.</summary>
    private static byte[]? PngToDib(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png);
        if (decoded is null) return null;

        using var bitmap = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
        var source = bitmap ?? decoded;

        const int header = 40;
        var width = source.Width;
        var height = source.Height;
        var row = width * 4;
        var data = new byte[header + row * height];

        fixed (byte* d = data)
        {
            *(uint*)d = header;
            *(int*)(d + 4) = width;
            *(int*)(d + 8) = height;
            *(ushort*)(d + 12) = 1;
            *(ushort*)(d + 14) = 32;
            *(uint*)(d + 20) = (uint)(row * height);
        }

        var pixels = source.GetPixelSpan();
        for (var y = 0; y < height; y++)
            pixels.Slice(y * source.RowBytes, row).CopyTo(data.AsSpan(header + (height - 1 - y) * row, row));

        return data;
    }

    // --------------------------------------------------------------- Writing

    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? WriteText(string text) => WriteText(text, null, null);

    /// <summary>A text with the formatting it was copied with; a program takes the richest format it reads.</summary>
    public static string? WriteText(string text, string? html, byte[]? rtf) => Write(() =>
    {
        if (!Put(CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0"))) return false;

        // A format that doesn't go on just leaves the plain text; no reason to fail the paste.
        if (html is { Length: > 0 } && HtmlFormat != 0) Put(HtmlFormat, [.. Encoding.UTF8.GetBytes(html), 0]);
        if (rtf is { Length: > 0 } && RtfFormat != 0) Put(RtfFormat, [.. rtf, 0]);
        return true;
    });

    /// <summary>Files as Explorer copies them: <c>CF_HDROP</c> plus "copy" as preferred effect.</summary>
    public static string? WriteFiles(IReadOnlyList<string> files)
    {
        // DROPFILES: offset of the list, a point, two flags; then the wide names, each ending with
        // a zero, the list ending with one more.
        const int header = 20;
        var names = Encoding.Unicode.GetBytes(string.Join('\0', files) + "\0\0");
        var data = new byte[header + names.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0), header);
        BitConverter.TryWriteBytes(data.AsSpan(16), 1);
        names.CopyTo(data, header);

        return Write(() => Put(CfHdrop, data) && Put(DropEffectFormat, BitConverter.GetBytes(1)));
    }

    /// <summary>An image as DIB and PNG, like a screenshot.</summary>
    public static string? WriteImage(byte[] png)
    {
        if (PngToDib(png) is not { } dib) return "The image could not be read.";
        return Write(() => Put(CfDib, dib) && Put(PngFormat, png));
    }

    private static string? Write(Func<bool> put)
    {
        var owner = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);

        try
        {
            if (!Open(owner)) return "The clipboard is in use by another program.";

            try
            {
                if (!EmptyClipboard()) return "The clipboard could not be cleared.";
                return put() ? null : "The entry could not be placed on the clipboard.";
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

    /// <summary>With short retries: another program may hold the clipboard for milliseconds.</summary>
    private static bool Open(nint owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(15);
        }

        return false;
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardData(uint format);

    [LibraryImport("user32.dll")]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    private static partial nint GetClipboardOwner();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("shell32.dll")]
    private static partial uint DragQueryFileW(nint drop, uint index, char* file, uint length);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nuint GlobalSize(nint memory);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint memory);
}

/// <summary>Gets told about every clipboard change: a message-only window on its own thread.</summary>
/// <remarks>
/// <c>WM_CLIPBOARDUPDATE</c> is sent, not posted, so it needs a window procedure; <c>GetMessage</c> alone
/// never sees it. The clipboard is read inside that procedure (a few milliseconds) and the result handed on;
/// converting and storing happen elsewhere.
/// </remarks>
internal sealed unsafe partial class ClipboardListener : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmQuit = 0x0012;
    private const nint HwndMessage = -3;
    private const string ClassName = "WinToClipboardListener";

    private static readonly Lock ClassGate = new();
    private static bool _classRegistered;
    private static ClipboardListener? _active;

    private readonly Action<ClipboardSnapshot> _changed;
    private readonly Thread _thread;
    private uint _threadId;

    public ClipboardListener(Action<ClipboardSnapshot> changed)
    {
        _changed = changed;
        _active = this;

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "WinTo clipboard" };
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        var instance = GetModuleHandleW(null);

        lock (ClassGate)
        {
            if (!_classRegistered)
            {
                fixed (char* name = ClassName)
                {
                    var type = new WindowClass
                    {
                        Size = (uint)sizeof(WindowClass),
                        Procedure = &Procedure,
                        Instance = instance,
                        ClassName = name,
                    };
                    _classRegistered = RegisterClassExW(&type) != 0;
                }
            }
        }

        var window = CreateWindowExW(0, ClassName, "", 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
        if (window != 0) AddClipboardFormatListener(window);
        ready.Set();

        if (window == 0) return;

        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }

        RemoveClipboardFormatListener(window);
        DestroyWindow(window);
    }

    [UnmanagedCallersOnly]
    private static nint Procedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message != WmClipboardUpdate) return DefWindowProcW(window, message, wParam, lParam);

        try
        {
            if (_active is { } listener && ClipboardNative.Read(window) is { } snapshot) listener._changed(snapshot);
        }
        catch (Exception ex)
        {
            new Services.ModuleLog("features").Warn($"Clipboard could not be read: {ex.Message}");
        }

        return 0;
    }

    public void Dispose()
    {
        if (_active == this) _active = null;
        if (_threadId != 0) PostThreadMessageW(_threadId, WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public char* MenuName;
        public char* ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(WindowClass* type);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddClipboardFormatListener(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveClipboardFormatListener(nint window);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(Msg* message);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(Msg* message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
