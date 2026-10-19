using System.Runtime.InteropServices;

namespace MultiTool.Host.Services;

/// <summary>Windows known folders that .NET's <see cref="Environment.SpecialFolder"/> doesn't cover.</summary>
internal static partial class KnownFolders
{
    private static readonly Guid ScreenshotsId = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    /// <summary>Where Win+PrtScn, the Snipping Tool and WinTo's capture save; follows a moved Pictures folder.</summary>
    public static string Screenshots
    {
        get
        {
            if (SHGetKnownFolderPath(ScreenshotsId, 0, 0, out var path) == 0 && path != 0)
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

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
