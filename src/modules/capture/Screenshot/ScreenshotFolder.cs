using System.Runtime.InteropServices;

namespace MultiTool.Modules.Capture.Screenshot;

/// <summary>Where screenshots go by default: the folder Windows itself uses.</summary>
/// <remarks>
/// <para>
/// <b>The known folder, not a fixed path.</b> <c>FOLDERID_Screenshots</c> follows a moved Pictures folder
/// and OneDrive backup; <c>Pictures\Screenshots</c> spelled out would miss both. Win+PrtScn and the Snipping
/// Tool save there, so WinTo's captures land next to theirs.
/// </para>
/// <para>
/// Until 0.12.0 the default was <c>Pictures\MultiTool Screenshots</c>. A setting holding exactly that path
/// was never chosen by anyone and counts as the default.
/// </para>
/// </remarks>
internal static partial class ScreenshotFolder
{
    private static readonly Guid Screenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    public static string Default
    {
        get
        {
            if (SHGetKnownFolderPath(Screenshots, 0, 0, out var path) == 0 && path != 0)
            {
                try
                {
                    if (Marshal.PtrToStringUni(path) is { Length: > 0 } folder) return folder;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(path);
                }
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        }
    }

    /// <summary>The stored folder, or the default when nothing or the old default is stored.</summary>
    public static string Resolve(string stored)
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MultiTool Screenshots");

        return stored.Length == 0 || string.Equals(Path.TrimEndingDirectorySeparator(stored), old, StringComparison.OrdinalIgnoreCase)
            ? Default
            : stored;
    }

    /// <summary>A new file name like Windows names its own: <c>Screenshot 2026-10-15 011804.png</c>.</summary>
    public static string NewFile(string folder)
    {
        var stem = $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}";
        var path = Path.Combine(folder, stem + ".png");

        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(folder, $"{stem} ({i}).png");

        return path;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
