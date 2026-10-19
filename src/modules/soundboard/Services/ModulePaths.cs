using MultiTool.Abstractions;

namespace MultiTool.Modules.Soundboard.Services;

/// <summary>Where the module's data lives.</summary>
/// <remarks>
/// Replaces the standalone app's static <c>AppPaths</c>. A module doesn't know its data folder itself, it gets
/// it from the host: <c>%APPDATA%\WinTo\modules\soundboard</c>, no longer <c>%APPDATA%\Soundbord</c>.
/// </remarks>
public sealed class ModulePaths
{
    /// <summary>Where the standalone app stored its data.</summary>
    private static readonly string LegacyDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Soundbord");

    public ModulePaths(IModuleContext context)
    {
        DataDirectory = context.DataDirectory;
        SettingsFile = Path.Combine(DataDirectory, "settings.json");
        LibraryFile = Path.Combine(DataDirectory, "sounds.json");

        MigrateFromStandalone(context.Log);
    }

    public string DataDirectory { get; }

    public string SettingsFile { get; }

    public string LibraryFile { get; }

    /// <summary>Adopts settings and sound list from the standalone app if they exist and nothing is here yet.</summary>
    /// <remarks>
    /// <para>
    /// <b>Copied, not moved.</b> Anyone keeping the old app can still use it, and if something was missed when
    /// porting, the old state is still there. The cost is two duplicate JSON files of a few KB.
    /// </para>
    /// <para>
    /// Only runs while nothing is here. Afterwards every further adoption would be a reset to an old state.
    /// </para>
    /// </remarks>
    private void MigrateFromStandalone(IModuleLog log)
    {
        try
        {
            if (File.Exists(SettingsFile) || File.Exists(LibraryFile)) return;
            if (!Directory.Exists(LegacyDirectory)) return;

            Directory.CreateDirectory(DataDirectory);

            var taken = 0;
            foreach (var name in new[] { "settings.json", "sounds.json" })
            {
                var from = Path.Combine(LegacyDirectory, name);
                if (!File.Exists(from)) continue;

                File.Copy(from, Path.Combine(DataDirectory, name), overwrite: false);
                taken++;
            }

            if (taken > 0)
                log.Info($"{taken} file(s) adopted from {LegacyDirectory}.");
        }
        catch (Exception ex)
        {
            // Without adoption the module starts with an empty list. Annoying, but no reason not to open it.
            log.Warn("Adopting the standalone soundboard data failed: " + ex.Message);
        }
    }
}
