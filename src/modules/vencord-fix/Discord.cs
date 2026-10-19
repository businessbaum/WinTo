using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace MultiTool.Modules.VencordFix;

/// <param name="Directory">Folder name under <c>%LOCALAPPDATA%</c>.</param>
/// <param name="Executable">The executable name, different per branch.</param>
public sealed record Branch(string Name, string Directory, string Executable);

public enum PatchState
{
    /// <summary>Correctly patched.</summary>
    Patched,

    /// <summary>Patched, but the reference points to a missing patcher.js.</summary>
    WrongPath,

    /// <summary>Stub without <c>_app.asar</c>; Discord won't start at all.</summary>
    Orphaned,

    /// <summary>Unmodified Discord.</summary>
    Vanilla,

    /// <summary><c>resources\app.asar</c> is missing.</summary>
    NoAsar,
}

public sealed record DiscordInstall(
    Branch Branch,
    string Root,
    string AppDirectory,
    Version Version,
    bool IsNewest)
{
    public string Resources => Path.Combine(AppDirectory, "resources");

    public string AppAsar => Path.Combine(Resources, "app.asar");

    public string OriginalAsar => Path.Combine(Resources, "_app.asar");

    public string ExecutablePath => Path.Combine(AppDirectory, Branch.Executable);

    public string Label => $"{Branch.Name} {Version}";
}

/// <summary>Finds Discord, determines its patch state and restores the patch.</summary>
/// <remarks>
/// <para>
/// Vencord patches Discord in one place: it renames <c>resources\app.asar</c> to <c>_app.asar</c> and
/// puts a 219-byte stub archive there containing one line,
/// <c>require("…\Vencord\dist\patcher.js")</c>. The patch lives <b>in the versioned folder</b>, and every
/// Discord update creates a new <c>app-&lt;version&gt;</c> folder with a fresh, unpatched <c>app.asar</c>.
/// </para>
/// <para>
/// Vencord's self-repair only runs on <c>before-quit</c>, i.e. a <i>clean</i> exit. Killed, crashed or
/// shut down while Discord ran: the new folder stays unpatched. That is what this module is for.
/// </para>
/// <para>
/// <b>The stub is byte-identical to the official installer's</b> (verified, 219 bytes). The format is a
/// Chromium pickle; the four length fields depend on each other as below, and one byte off makes
/// Electron reject the archive.
/// </para>
/// </remarks>
internal static class Discord
{
    public static readonly Branch[] Branches =
    [
        new("Stable", "Discord", "Discord.exe"),
        new("PTB", "DiscordPTB", "DiscordPTB.exe"),
        new("Canary", "DiscordCanary", "DiscordCanary.exe"),
        new("Development", "DiscordDevelopment", "DiscordDevelopment.exe"),
    ];

    private const string SettingsKey = @"Software\VencordGuard";

    // ------------------------------------------------------------------ find

    public static List<DiscordInstall> Find()
    {
        var result = new List<DiscordInstall>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var branch in Branches)
        {
            var root = Path.Combine(localAppData, branch.Directory);
            if (!Directory.Exists(root)) continue;

            var found = new List<(Version Version, string Directory)>();

            foreach (var directory in Directory.GetDirectories(root, "app-*"))
            {
                var name = Path.GetFileName(directory);
                if (!Version.TryParse(name[4..], out var version)) continue;

                // Skip empty leftover folders: Squirrel doesn't always clean up, and they have no EXE.
                if (!File.Exists(Path.Combine(directory, branch.Executable))) continue;

                found.Add((version, directory));
            }

            found.Sort((a, b) => b.Version.CompareTo(a.Version));

            for (var i = 0; i < found.Count; i++)
                result.Add(new DiscordInstall(branch, root, found[i].Directory, found[i].Version, IsNewest: i == 0));
        }

        return result;
    }

    public static bool IsRunning(DiscordInstall install) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(install.Branch.Executable)).Length > 0;

    /// <summary>Whether Discord runs <b>from this version's folder</b>, which is what keeps its app.asar open.</summary>
    /// <remarks>
    /// An update lands in a new folder while the old version keeps running; that folder can be patched
    /// meanwhile. A process whose path can't be read counts as running from here: better skip than break.
    /// </remarks>
    public static bool IsRunningFrom(DiscordInstall install)
    {
        var folder = install.AppDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(install.Branch.Executable)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is not { } file
                        || file.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ state

    public static string VencordDataDirectory
    {
        get
        {
            var custom = Environment.GetEnvironmentVariable("VENCORD_USER_DATA_DIR");
            if (!string.IsNullOrEmpty(custom)) return custom;

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vencord");
        }
    }

    private static string? _patcherOverride;

    /// <summary>Which <c>patcher.js</c> is used.</summary>
    /// <remarks>
    /// Default is the installer build under <c>%APPDATA%\Vencord\dist</c>. A Vencord built from source
    /// (needed for plugins in <c>src/userplugins</c>) points to <c>&lt;repo&gt;\dist\patcher.js</c> instead.
    /// That path is remembered once seen in a working patch, so the next Discord version gets the same
    /// build.
    /// </remarks>
    public static string PatcherPath
    {
        get
        {
            if (_patcherOverride is not null) return _patcherOverride;

            using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                if (key?.GetValue("PatcherPath") is string stored && File.Exists(stored))
                    return _patcherOverride = stored;
            }

            return _patcherOverride = Path.Combine(VencordDataDirectory, "dist", "patcher.js");
        }
    }

    public static void RememberPatcher(string path)
    {
        if (string.Equals(path, PatcherPath, StringComparison.OrdinalIgnoreCase)) return;

        using var key = Registry.CurrentUser.CreateSubKey(SettingsKey);
        key.SetValue("PatcherPath", path);
        _patcherOverride = path;
    }

    /// <summary>A self-built Vencord, never overwritten.</summary>
    public static bool IsCustomBuild =>
        !PatcherPath.StartsWith(VencordDataDirectory, StringComparison.OrdinalIgnoreCase);

    public static PatchState StateOf(DiscordInstall install)
    {
        if (!File.Exists(install.AppAsar)) return PatchState.NoAsar;

        var referenced = ReferencedPatcher(install);
        if (referenced is null) return PatchState.Vanilla;

        // Stub without backup: the patcher can't find the original and Discord doesn't start. Worse
        // than unpatched.
        if (!File.Exists(install.OriginalAsar)) return PatchState.Orphaned;

        if (string.Equals(referenced, PatcherPath, StringComparison.OrdinalIgnoreCase))
            return PatchState.Patched;

        // Another path whose file exists is a deliberately set custom build: adopt it, don't touch it.
        return File.Exists(referenced) ? PatchState.Patched : PatchState.WrongPath;
    }

    /// <summary>Reads the path a patched <c>app.asar</c> points to.</summary>
    /// <returns><c>null</c> if the file is not a Vencord stub.</returns>
    public static string? ReferencedPatcher(DiscordInstall install)
    {
        try
        {
            if (!File.Exists(install.AppAsar)) return null;

            // The real app.asar is several megabytes; beyond this limit there's no need to look inside.
            if (new FileInfo(install.AppAsar).Length > 8192) return null;

            var text = Encoding.UTF8.GetString(File.ReadAllBytes(install.AppAsar));

            const string prefix = "require(\"";
            var start = text.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0) return null;

            start += prefix.Length;
            var end = text.IndexOf("\")", start, StringComparison.Ordinal);
            if (end < 0) return null;

            return text[start..end].Replace("\\\\", "\\");
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ patch

    /// <returns>A sentence for display, and whether anything changed.</returns>
    public static (bool Changed, string Message) Patch(DiscordInstall install)
    {
        switch (StateOf(install))
        {
            case PatchState.Patched:
                return (false, $"{install.Label}: already patched.");

            case PatchState.NoAsar:
                return (false, $"{install.Label}: resources\\app.asar missing, skipped.");

            case PatchState.Orphaned:
                return (false, $"{install.Label}: stub without _app.asar. Discord needs to be reinstalled.");

            case PatchState.WrongPath:
                // The backup exists, only the reference points nowhere. A new stub is enough; the original
                // stays untouched.
                try
                {
                    File.WriteAllBytes(install.AppAsar, BuildStub(PatcherPath));
                    return (true, $"{install.Label}: patcher.js reference fixed.");
                }
                catch (Exception ex)
                {
                    return (false, $"{install.Label}: {ex.Message}");
                }

            case PatchState.Vanilla:
                if (IsRunningFrom(install))
                    return (false, $"{install.Label}: Discord is running and holds app.asar open. Quit it first.");

                // An orphaned _app.asar from an earlier patch blocks the rename.
                if (File.Exists(install.OriginalAsar))
                {
                    try { File.Delete(install.OriginalAsar); }
                    catch (Exception ex) { return (false, $"{install.Label}: old _app.asar can't be deleted: {ex.Message}"); }
                }

                try { File.Move(install.AppAsar, install.OriginalAsar); }
                catch (Exception ex) { return (false, $"{install.Label}: app.asar can't be renamed: {ex.Message}"); }

                try
                {
                    File.WriteAllBytes(install.AppAsar, BuildStub(PatcherPath));
                }
                catch (Exception ex)
                {
                    // Roll back. Discord must never be left unable to start; unpatched beats broken.
                    try { File.Move(install.OriginalAsar, install.AppAsar); } catch (Exception) { }
                    return (false, $"{install.Label}: stub not writable, rolled back: {ex.Message}");
                }

                CleanLegacyModulePatch(install);
                return (true, $"{install.Label}: patched.");

            default:
                return (false, "");
        }
    }

    /// <summary>Undoes the patch; Discord then runs without Vencord.</summary>
    public static (bool Changed, string Message) Unpatch(DiscordInstall install)
    {
        if (StateOf(install) is PatchState.Vanilla or PatchState.NoAsar)
            return (false, $"{install.Label}: was not patched.");

        if (IsRunning(install))
            return (false, $"{install.Label}: Discord is running. Quit it first.");

        if (!File.Exists(install.OriginalAsar))
            return (false, $"{install.Label}: _app.asar missing, nothing to restore.");

        try
        {
            File.Delete(install.AppAsar);
            File.Move(install.OriginalAsar, install.AppAsar);
            return (true, $"{install.Label}: Vencord removed.");
        }
        catch (Exception ex)
        {
            return (false, $"{install.Label}: {ex.Message}");
        }
    }

    /// <summary>Removes leftovers of older Vencord versions.</summary>
    /// <remarks>
    /// Vencord used to patch <c>modules\discord_desktop_core-N\…\index.js</c> instead of <c>app.asar</c>.
    /// Such leftovers collide with today's patch: both load the patcher and Vencord registers twice.
    /// </remarks>
    private static void CleanLegacyModulePatch(DiscordInstall install)
    {
        var modules = Path.Combine(install.AppDirectory, "modules");
        if (!Directory.Exists(modules)) return;

        foreach (var core in Directory.GetDirectories(modules, "discord_desktop_core-*"))
        {
            var index = Path.Combine(core, "discord_desktop_core", "index.js");
            if (!File.Exists(index)) continue;

            try
            {
                if (File.ReadAllText(index).Contains("patcher.js", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(index, "module.exports = require('./core.asar');");
            }
            catch (Exception)
            {
                // A leftover old patch rarely hurts; aborting here would leave the fresh stub unreported.
            }
        }
    }

    // ------------------------------------------------------------------ launch

    /// <summary>Starts Discord the way its own shortcut does.</summary>
    /// <remarks>
    /// Via <c>Update.exe --processStart</c>, not the EXE directly, so Discord reaches its updater and
    /// keeps its process chain. Without the updater, the direct way remains.
    /// </remarks>
    public static string Launch(DiscordInstall install)
    {
        var updater = Path.Combine(install.Root, "Update.exe");

        var info = File.Exists(updater)
            ? new ProcessStartInfo(updater) { UseShellExecute = false }
            : new ProcessStartInfo(install.ExecutablePath) { UseShellExecute = false };

        if (File.Exists(updater))
        {
            info.ArgumentList.Add("--processStart");
            info.ArgumentList.Add(install.Branch.Executable);
        }

        Process.Start(info);
        return $"{install.Label} started.";
    }

    // ------------------------------------------------------------------ asar

    /// <summary>Builds the stub archive, byte-identical to the official installer's.</summary>
    /// <remarks>
    /// Chromium pickle format:
    /// <code>
    ///   UInt32  4                       size of the next field
    ///   UInt32  headerPickleSize        = headerStringPickleSize + 4
    ///   UInt32  headerStringPickleSize  = align4(headerLen) + 4
    ///   UInt32  headerLen
    ///   bytes   header JSON
    ///   bytes   padding to the next 4-byte boundary
    ///   bytes   file contents in header order
    /// </code>
    /// The tabs and newlines in <c>package.json</c> are deliberate: that is how the Go installer writes
    /// it, and only then do the bytes match.
    /// </remarks>
    public static byte[] BuildStub(string patcherPath)
    {
        var indexJs = Encoding.UTF8.GetBytes($"require(\"{patcherPath.Replace("\\", "\\\\")}\")");
        var packageJson = Encoding.UTF8.GetBytes("{\n\t\"name\": \"discord\",\n\t\"main\": \"index.js\"\n}");

        return BuildAsar([("index.js", indexJs), ("package.json", packageJson)]);
    }

    private static byte[] BuildAsar(IReadOnlyList<(string Name, byte[] Content)> files)
    {
        var json = new StringBuilder("{\"files\":{");
        var body = new MemoryStream();
        var offset = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var (name, content) = files[i];

            if (i > 0) json.Append(',');
            json.Append('"').Append(name).Append("\":{\"size\":").Append(content.Length)
                .Append(",\"offset\":\"").Append(offset).Append("\"}");

            body.Write(content);
            offset += content.Length;
        }

        json.Append("}}");

        var header = Encoding.UTF8.GetBytes(json.ToString());
        var aligned = (header.Length + 3) / 4 * 4;

        var output = new MemoryStream();
        var writer = new BinaryWriter(output);

        writer.Write(4u);
        writer.Write((uint)(aligned + 8));
        writer.Write((uint)(aligned + 4));
        writer.Write((uint)header.Length);
        writer.Write(header);

        for (var i = header.Length; i < aligned; i++) writer.Write((byte)0);

        writer.Write(body.ToArray());
        writer.Flush();

        return output.ToArray();
    }
}
