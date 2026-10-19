namespace MultiTool.Abstractions;

/// <summary>
/// Places a module must never delete, not even to the recycle bin. Since 0.9.0.
/// </summary>
/// <remarks>
/// <para>
/// Installers write their install location themselves, and some write nonsense such as
/// <c>C:\Program Files</c>, the user folder or a whole drive. Shared by the uninstaller and
/// disk usage, which offers "move to recycle bin" for any folder it shows.
/// </para>
/// <para>
/// Protected: every drive root, everything under the Windows folder, every known folder
/// (Program Files, ProgramData, AppData, Desktop, Documents, Downloads…) <b>and everything
/// above it</b>, and every user profile. Things <i>inside</i> a known folder are not
/// protected: <c>C:\Program Files\SomeGame</c> may go.
/// </para>
/// </remarks>
public static class ProtectedPaths
{
    /// <summary>Must this place not be deleted?</summary>
    /// <returns><c>true</c> also when the path cannot be resolved.</returns>
    public static bool IsProtected(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return true;
        }

        // A drive root.
        if (Path.GetPathRoot(full) is { } root
            && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length > 0 && IsSameOrBelow(full, windows)) return true;

        foreach (var known in KnownFolders.Value)
        {
            // The place itself or something above it.
            if (IsSameOrBelow(known, full)) return true;
        }

        // Any user profile, including other users': its parent is the profiles folder.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.GetDirectoryName(profile) is { Length: > 0 } profiles
            && string.Equals(Path.GetDirectoryName(full), profiles, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Is <paramref name="path"/> the same as <paramref name="root"/> or below it?</summary>
    private static bool IsSameOrBelow(string path, string root)
    {
        var a = Path.TrimEndingDirectorySeparator(path);
        var b = Path.TrimEndingDirectorySeparator(root);

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Lazy<string[]> KnownFolders = new(() =>
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.MyMusic,
            Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.StartMenu,
            Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms,
            Environment.SpecialFolder.Startup,
            Environment.SpecialFolder.CommonStartup,
            Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.CommonDocuments,
            Environment.SpecialFolder.Templates,
            Environment.SpecialFolder.Fonts,
        ];

        var paths = folders
            .Select(Environment.GetFolderPath)
            .Where(p => p.Length > 0)
            .ToList();

        // Not in SpecialFolder, but everyone has them.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (profile.Length > 0)
        {
            paths.Add(Path.Combine(profile, "Downloads"));
            paths.Add(Path.Combine(profile, "AppData"));
            paths.Add(Path.Combine(profile, "AppData", "LocalLow"));
            paths.Add(Path.Combine(profile, "OneDrive"));
        }

        if (local.Length > 0)
        {
            paths.Add(Path.Combine(local, "Programs"));
            paths.Add(Path.Combine(local, "Temp"));
        }

        return [.. paths.Select(Path.TrimEndingDirectorySeparator).Distinct(StringComparer.OrdinalIgnoreCase)];
    });
}
