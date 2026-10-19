using Avalonia.Threading;
using MultiTool.Host.Services;
using MultiTool.Host.ViewModels;

namespace MultiTool.Host.Features;

/// <summary>Patches Vencord into a new Discord version as soon as Discord downloads it.</summary>
/// <remarks>
/// <para>
/// <b>Why this works in the background:</b> Discord (Squirrel) downloads an update into a new
/// <c>app-&lt;version&gt;</c> folder while the old version keeps running, and switches to it at the next
/// start, often hours later. The new folder's <c>app.asar</c> is not in use until then, so it can be patched
/// in between and the next start already has Vencord.
/// </para>
/// <para>
/// <b>Only branches seen patched.</b> Per branch the newest version seen with the Vencord stub is stored. A
/// newer folder without the stub is an update and gets repaired; the same version without it was unpatched
/// on purpose and is forgotten. A Discord never patched is never touched.
/// </para>
/// <para>
/// <b>Cheap here, heavy on demand:</b> this class only watches the folders and compares file sizes (the stub
/// is 219 bytes, a real <c>app.asar</c> megabytes). Patching itself is the Vencord tool's code, loaded quietly
/// with "auto" and released when done.
/// </para>
/// </remarks>
public sealed class VencordGuardFeature(ShellViewModel shell) : Feature(shell)
{
    private static readonly string[] Branches = ["Discord", "DiscordPTB", "DiscordCanary", "DiscordDevelopment"];

    /// <summary>A real <c>app.asar</c> is megabytes; anything this small is the stub.</summary>
    private const long StubLimit = 8192;

    /// <summary>Squirrel extracts for a while after creating the folder.</summary>
    private static readonly TimeSpan AfterUpdate = TimeSpan.FromSeconds(90);

    private readonly List<FileSystemWatcher> _watchers = [];
    private DispatcherTimer? _timer;
    private bool _checking;

    public override string Id => "vencord-guard";

    public override string Name => "Keep Vencord";

    public override string Description => "Patches Vencord into a new Discord version right after Discord downloads it.";

    public override string Group => "Apps";

    /// <summary>The Vencord tool's icon from its manifest; no second copy here.</summary>
    protected override string IconPath => Shell.Catalog.Find(ModuleIds.VencordFix)?.Icon ?? "";

    protected override bool ReadEnabled() => Settings.VencordGuard;

    protected override void StoreEnabled(bool value) => Settings.VencordGuard = value;

    protected override void Start()
    {
        if (Shell.Catalog.Find(ModuleIds.VencordFix) is null)
            throw new InvalidOperationException("The Vencord tool is not installed.");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var branch in Branches)
        {
            var root = Path.Combine(localAppData, branch);
            if (!Directory.Exists(root)) continue;

            var watcher = new FileSystemWatcher(root, "app-*")
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
            };
            watcher.Created += (_, _) => Dispatcher.UIThread.Post(() => Schedule(AfterUpdate));
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }

        _timer = new DispatcherTimer();
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = CheckAsync();
        };

        Detail = _watchers.Count == 0 ? "No Discord found" : "Watching Discord";

        // Once shortly after start: an update may have arrived while WinTo wasn't running.
        Schedule(TimeSpan.FromSeconds(45));
    }

    protected override void Stop()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();

        _timer?.Stop();
        _timer = null;
        Detail = "Off";
    }

    private void Schedule(TimeSpan delay)
    {
        if (_timer is null) return;

        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private async Task CheckAsync()
    {
        if (_checking || _timer is null) return;
        _checking = true;

        try
        {
            var known = new Dictionary<string, string>(Settings.VencordPatched, StringComparer.OrdinalIgnoreCase);
            var (repair, changed) = await Task.Run(() => Compare(known));

            if (changed)
            {
                Settings.VencordPatched = known;
                Shell.SaveSettings();
            }

            if (repair.Count > 0 && _timer is not null)
            {
                new ModuleLog("features").Info($"Discord updated without Vencord: {string.Join(", ", repair)}. Repairing.");
                Shell.OpenModule(ModuleIds.VencordFix, argument: "auto", quiet: true);
            }

            Detail = known.Count == 0 ? "No patched Discord seen yet" : $"Keeps {string.Join(", ", known.Keys.Select(Label))} patched";
        }
        catch (Exception ex)
        {
            new ModuleLog("features").Warn($"Vencord check failed: {ex.Message}");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Updates the remembered versions and returns the branches that lost their patch to an update.</summary>
    private static (List<string> Repair, bool Changed) Compare(Dictionary<string, string> known)
    {
        var repair = new List<string>();
        var changed = false;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var branch in Branches)
        {
            if (Newest(Path.Combine(localAppData, branch), branch + ".exe") is not { } newest) continue;

            var patched = newest.AsarLength is > 0 and <= StubLimit;
            var seen = known.TryGetValue(branch, out var text) && Version.TryParse(text, out var version) ? version : null;

            if (patched)
            {
                if (seen != newest.Version)
                {
                    known[branch] = newest.Version.ToString();
                    changed = true;
                }
            }
            else if (seen is not null && newest.Version > seen && newest.AsarLength > 0)
            {
                repair.Add(Label(branch));
            }
            else if (seen is not null && newest.Version == seen)
            {
                // Unpatched on purpose: stop keeping it patched.
                known.Remove(branch);
                changed = true;
            }
        }

        return (repair, changed);
    }

    private static (Version Version, long AsarLength)? Newest(string root, string executable)
    {
        if (!Directory.Exists(root)) return null;

        (Version Version, string Directory)? best = null;

        foreach (var directory in Directory.GetDirectories(root, "app-*"))
        {
            if (!Version.TryParse(Path.GetFileName(directory)[4..], out var version)) continue;
            if (!File.Exists(Path.Combine(directory, executable))) continue;
            if (best is null || version > best.Value.Version) best = (version, directory);
        }

        if (best is not { } found) return null;

        var asar = new FileInfo(Path.Combine(found.Directory, "resources", "app.asar"));
        return (found.Version, asar.Exists ? asar.Length : 0);
    }

    private static string Label(string branch) => branch == "Discord" ? "Stable" : branch["Discord".Length..];
}
