using System.Runtime.InteropServices;

namespace MultiTool.Abstractions;

/// <summary>
/// Moves files to the recycle bin instead of deleting them.
/// </summary>
/// <remarks>
/// <para>
/// Modules delete based on a judgement (a checksum, a name match) and the user confirms a list
/// they did not read line by line. With <c>File.Delete</c> a mistake is final; here it is a
/// right-click restore.
/// </para>
/// <para>
/// <c>SHFileOperation</c> rather than <c>IFileOperation</c>: no STA or window handle needed, and
/// it behaves exactly like Explorer. <c>NOCONFIRMATION</c> skips Windows' own prompt (the module
/// already asked); <c>WANTNUKEWARNING</c> brings back the one prompt that matters, when a file is
/// too large for the bin and would be deleted permanently.
/// </para>
/// </remarks>
public static partial class RecycleBin
{
    private const uint FoDelete = 0x0003;

    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;
    private const ushort FofWantNukeWarning = 0x4000;

    /// <summary>Moves all paths to the recycle bin in one operation.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? Delete(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return null;

        // One buffer: each path null-terminated, plus a final null.
        var block = string.Concat(paths.Select(p => p + '\0')) + '\0';
        var buffer = Marshal.StringToHGlobalUni(block);

        try
        {
            var operation = new FileOpStruct
            {
                wFunc = FoDelete,
                pFrom = buffer,
                fFlags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi | FofWantNukeWarning,
            };

            var result = SHFileOperationW(ref operation);

            // The user declined the permanent-delete warning.
            if (operation.fAnyOperationsAborted != 0) return "Cancelled.";

            return result == 0 ? null : $"Windows reported error {result}.";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <remarks>All fields blittable (<c>int</c> instead of <c>bool</c>) so LibraryImport can marshal it.</remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileOpStruct
    {
        public nint hwnd;
        public uint wFunc;
        public nint pFrom;
        public nint pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public nint lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    private static partial int SHFileOperationW(ref FileOpStruct operation);
}
