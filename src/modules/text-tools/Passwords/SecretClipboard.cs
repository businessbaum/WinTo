using System.Runtime.InteropServices;

namespace MultiTool.Modules.TextTools.Passwords;

/// <summary>Puts a password into the clipboard so that it stays no longer than needed.</summary>
/// <remarks>
/// <para>
/// As plain text it would land in the Windows clipboard history (Win+V), optionally the cloud sync,
/// and stay until something else was copied.
/// </para>
/// <para>
/// Windows knows three extra formats, also set by password managers:
/// <c>ExcludeClipboardContentFromMonitorProcessing</c> keeps history and monitoring tools away,
/// <c>CanIncludeInClipboardHistory</c> and <c>CanUploadToCloudClipboard</c> set to 0 block history and
/// cloud. Avalonia's clipboard only sets text, hence user32 directly.
/// </para>
/// <para>
/// Clearing checks the clipboard sequence number: if someone copied something else meanwhile, it is
/// left alone.
/// </para>
/// </remarks>
internal static partial class SecretClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    private static readonly uint ExcludeFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private static readonly uint CloudFormat = RegisterClipboardFormatW("CanUploadToCloudClipboard");

    /// <summary>Puts the text in.</summary>
    /// <param name="owner">A window of the app; without owner Windows rejects <c>SetClipboardData</c>.</param>
    /// <returns>The sequence number afterwards, or <c>null</c> on failure.</returns>
    public static uint? Set(nint owner, string text)
    {
        if (owner == 0 || !Open(owner)) return null;

        try
        {
            if (!EmptyClipboard()) return null;

            var chars = (text.Length + 1) * sizeof(char);
            if (!Put(CfUnicodeText, chars, memory => Marshal.Copy((text + '\0').ToCharArray(), 0, memory, text.Length + 1)))
                return null;

            // The three markers. If one is missing, the password is still copied, just as before.
            Put(ExcludeFormat, sizeof(int), memory => Marshal.WriteInt32(memory, 0));
            Put(HistoryFormat, sizeof(int), memory => Marshal.WriteInt32(memory, 0));
            Put(CloudFormat, sizeof(int), memory => Marshal.WriteInt32(memory, 0));
        }
        finally
        {
            CloseClipboard();
        }

        return GetClipboardSequenceNumber();
    }

    /// <summary>Clears the clipboard if nothing else was put in since <paramref name="sequence"/>.</summary>
    /// <remarks>No owner window: the original may be closed, and clearing needs none.</remarks>
    public static void ClearIfUnchanged(uint sequence)
    {
        if (GetClipboardSequenceNumber() != sequence || !Open(0)) return;

        try
        {
            EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Opens with short retries: another program (a history tool, remote desktop) may hold
    /// the clipboard for milliseconds.</summary>
    private static bool Open(nint owner)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(owner)) return true;
            Thread.Sleep(15);
        }

        return false;
    }

    private static bool Put(uint format, int bytes, Action<nint> fill)
    {
        if (format == 0) return false;

        var handle = GlobalAlloc(GmemMoveable, (nuint)bytes);
        if (handle == 0) return false;

        var memory = GlobalLock(handle);
        if (memory == 0)
        {
            GlobalFree(handle);
            return false;
        }

        try
        {
            fill(memory);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        // After success the memory belongs to the system and must not be freed.
        if (SetClipboardData(format, handle) != 0) return true;

        GlobalFree(handle);
        return false;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GlobalFree(nint memory);
}
