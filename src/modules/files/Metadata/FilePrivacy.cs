using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MultiTool.Modules.Files.Metadata;

internal static class FilePrivacy
{
    internal static void ResetTimes(string path)
    {
        var neutral = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(path, neutral);
        File.SetLastWriteTimeUtc(path, neutral);
        File.SetLastAccessTimeUtc(path, neutral);
    }

    internal static IReadOnlyList<MetadataEntry> Streams(string path)
    {
        var entries = new List<MetadataEntry>();
        if (!OperatingSystem.IsWindows()) return entries;
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle == new IntPtr(-1))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 38 or 1 or 50) return entries;
            throw new Win32Exception(error);
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA") entries.Add(new("File system", data.Name, $"{data.Size} Bytes", true));
            } while (FindNextStreamW(handle, out data));
            if (Marshal.GetLastWin32Error() != 38) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { FindClose(handle); }
        return entries;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string file, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStreamW(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}
