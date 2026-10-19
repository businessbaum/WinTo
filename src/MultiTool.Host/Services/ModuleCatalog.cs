using MultiTool.Abstractions;

namespace MultiTool.Host.Services;

/// <summary>Which modules exist, determined from <c>module.json</c> alone.</summary>
/// <remarks>
/// <para>
/// The catalog reads only JSON at startup: no module assembly is loaded, no type resolved, no
/// module code run. More modules only means more small files.
/// </para>
/// <para>
/// Broken manifests are collected and shown on the home page, never skipped silently.
/// </para>
/// </remarks>
public sealed class ModuleCatalog
{
    private readonly Dictionary<string, ModuleManifest> _byId;

    private ModuleCatalog(IReadOnlyList<ModuleManifest> modules, IReadOnlyList<CatalogProblem> problems)
    {
        Modules = modules;
        Problems = problems;
        _byId = modules.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ModuleManifest> Modules { get; }

    /// <summary>Manifests that could not be read. Shown on screen.</summary>
    public IReadOnlyList<CatalogProblem> Problems { get; }

    public ModuleManifest? Find(string id) =>
        _byId.TryGetValue(id, out var m) ? m : null;

    public static ModuleCatalog Discover()
    {
        var found = new List<ModuleManifest>();
        var problems = new List<CatalogProblem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in AppPaths.ModuleRoots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var manifestPath = Path.Combine(dir, "module.json");
                if (!File.Exists(manifestPath)) continue;

                ModuleManifest? manifest;
                try
                {
                    manifest = System.Text.Json.JsonSerializer.Deserialize<ModuleManifest>(
                        File.ReadAllText(manifestPath), JsonStore.Options);
                }
                catch (Exception ex)
                {
                    problems.Add(new CatalogProblem(manifestPath, ex.Message));
                    continue;
                }

                if (manifest is null)
                {
                    problems.Add(new CatalogProblem(manifestPath, "The file is empty."));
                    continue;
                }

                manifest.Directory = dir;

                // The folder name is the id; installer, settings and status files all address modules by it.
                var folder = Path.GetFileName(dir);
                if (!string.Equals(folder, manifest.Id, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add(new CatalogProblem(manifestPath,
                        $"Folder name \"{folder}\" and manifest id \"{manifest.Id}\" do not match."));
                    continue;
                }

                if (RequiresNewerHost(manifest, out var required))
                {
                    problems.Add(new CatalogProblem(manifestPath,
                        $"{manifest.Name} needs WinTo {required} or newer."));
                    continue;
                }

                // Only check that the file exists, never load it here.
                if (!File.Exists(manifest.AssemblyPath))
                {
                    problems.Add(new CatalogProblem(manifestPath,
                        $"Assembly missing: {manifest.Assembly}"));
                    continue;
                }

                // First match wins: bundled before user-added, both before the source tree.
                if (!seen.Add(manifest.Id)) continue;

                found.Add(manifest);
            }
        }

        found.Sort(static (a, b) =>
        {
            var byCategory = string.Compare(a.Category, b.Category, StringComparison.CurrentCultureIgnoreCase);
            if (byCategory != 0) return byCategory;
            var byOrder = a.Order.CompareTo(b.Order);
            return byOrder != 0 ? byOrder
                : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        return new ModuleCatalog(found, problems);
    }

    /// <summary>Whether the module needs a newer host than this one.</summary>
    /// <remarks>Turns a later <c>MissingMethodException</c> into a sentence on the home page.</remarks>
    private static bool RequiresNewerHost(ModuleManifest manifest, out Version required)
    {
        required = new Version(0, 0);

        if (string.IsNullOrWhiteSpace(manifest.MinHostVersion)) return false;
        if (!Version.TryParse(manifest.MinHostVersion, out var parsed)) return false;

        required = parsed;
        var host = typeof(ModuleCatalog).Assembly.GetName().Version ?? new Version(0, 0);
        return host < parsed;
    }
}

/// <param name="Path">The manifest that could not be read.</param>
/// <param name="Message">Why, in plain words.</param>
public sealed record CatalogProblem(string Path, string Message);
