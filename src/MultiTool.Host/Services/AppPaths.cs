namespace MultiTool.Host.Services;

/// <summary>Where everything lives, installed or running from the source tree.</summary>
public static class AppPaths
{
    /// <summary>Folder of the EXE. <b>Read-only</b> in a real install.</summary>
    public static string AppDirectory { get; } =
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    private static readonly string RoamingData =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary><c>%APPDATA%\WinTo</c>, the only place written to.</summary>
    /// <remarks>
    /// Until 0.13.0 the app was called MultiTool. Its folder is used where it exists and no WinTo folder does,
    /// until <see cref="MoveFormerData"/> renames it on a real start.
    /// </remarks>
    public static string DataDirectory { get; private set; } = ResolveDataDirectory();

    private static string ResolveDataDirectory()
    {
        var current = Path.Combine(RoamingData, "WinTo");
        var former = Path.Combine(RoamingData, "MultiTool");
        return !Directory.Exists(current) && Directory.Exists(former) ? former : current;
    }

    /// <summary>Renames <c>%APPDATA%\MultiTool</c> to <c>%APPDATA%\WinTo</c> once.</summary>
    /// <remarks>
    /// Only on a real start holding the single-instance slot, never from the self-test or a helper: a rename
    /// moves settings, module data and the clipboard history together. A folder in use by an old instance
    /// can't be renamed; it stays in use and the next start tries again.
    /// </remarks>
    public static void MoveFormerData()
    {
        var current = Path.Combine(RoamingData, "WinTo");
        var former = Path.Combine(RoamingData, "MultiTool");
        if (Directory.Exists(current) || !Directory.Exists(former)) return;

        try
        {
            Directory.Move(former, current);
            DataDirectory = current;
        }
        catch (Exception)
        {
            // In use or not permitted: the former folder keeps working.
        }
    }

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string CrashLogFile => Path.Combine(LogDirectory, "crash.log");

    /// <summary>Cached tile states, one file per module.</summary>
    public static string StatusDirectory => Path.Combine(DataDirectory, "status");

    /// <summary>A module's data folder. Created on demand.</summary>
    public static string ModuleData(string moduleId) =>
        Path.Combine(DataDirectory, "modules", moduleId);

    /// <summary>Bundled modules, next to the EXE.</summary>
    public static string BundledModules => Path.Combine(AppDirectory, "modules");

    /// <summary>Folders searched for modules.</summary>
    /// <remarks>
    /// <para>
    /// Unelevated: bundled next to the EXE, user-added under <c>%LOCALAPPDATA%</c> (Program Files
    /// is not writable without rights), and <c>build\modules</c> in the source tree. The first
    /// match per module id wins.
    /// </para>
    /// <para>
    /// <b>Elevated: only the app folder.</b> <c>%LOCALAPPDATA%</c> is user-writable; if it were
    /// searched elevated, any unprivileged program could drop a DLL there and wait for the next
    /// elevation prompt that names WinTo. Whoever can write the app folder can replace the EXE
    /// anyway.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ModuleRoots()
    {
        if (Elevation.IsElevated) return [BundledModules];

        var roots = new List<string>(3)
        {
            BundledModules,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinTo", "modules"),
        };

        if (RepoRoot() is { } repo) roots.Add(Path.Combine(repo, "build", "modules"));

        return roots;
    }

    /// <summary>Whether the path is under the app folder; second check to <see cref="ModuleRoots"/>, on the file itself.</summary>
    public static bool IsInsideAppDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(AppDirectory) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Walks up from the EXE to <c>MultiTool.slnx</c>. Not found means installed.</summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "MultiTool.slnx")))
                return dir.FullName;
        return null;
    }

    public static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
