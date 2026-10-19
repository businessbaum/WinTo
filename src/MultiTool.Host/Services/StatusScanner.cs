using System.Diagnostics;
using MultiTool.Abstractions;

namespace MultiTool.Host.Services;

/// <summary>Evaluates the manifest status probes without loading a single module assembly.</summary>
/// <remarks>
/// <para>
/// One pass reads the process list once and answers all process probes with it.
/// </para>
/// <para>
/// Runs after the first frame on a background thread; a slow probe (a sleeping network drive)
/// only delays its own tile.
/// </para>
/// </remarks>
public sealed class StatusScanner
{
    private readonly HashSet<string> _running;

    private StatusScanner(HashSet<string> running) => _running = running;

    public static StatusScanner Snapshot()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try { running.Add(p.ProcessName); }
                catch (Exception) { /* Process exited in the meantime. */ }
                finally { p.Dispose(); }
            }
        }
        catch (Exception)
        {
            // Without a process list, process probes stay unknown instead of wrong.
        }

        return new StatusScanner(running);
    }

    public ModuleStatus? Evaluate(ModuleManifest manifest)
    {
        try
        {
            return manifest.Status.Kind switch
            {
                StatusProbeKind.None => null,
                StatusProbeKind.Cached => StatusStore.Read(manifest.Id),
                StatusProbeKind.Process => EvaluateProcess(manifest.Status),
                StatusProbeKind.Path => EvaluatePath(manifest.Status),
                StatusProbeKind.Count => EvaluateCount(manifest.Status),
                StatusProbeKind.Tools => EvaluateTools(manifest),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            return new ModuleStatus(StatusLevel.Warn, "Status unknown", DateTimeOffset.Now, ex.Message);
        }
    }

    private ModuleStatus? EvaluateProcess(StatusProbe probe)
    {
        if (string.IsNullOrWhiteSpace(probe.Process)) return null;

        var name = probe.Process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? probe.Process[..^4]
            : probe.Process;

        var hit = _running.Contains(name);
        return new ModuleStatus(
            hit ? StatusLevel.Good : StatusLevel.Neutral,
            hit ? probe.WhenTrue ?? "running" : probe.WhenFalse ?? "not started",
            DateTimeOffset.Now);
    }

    private static ModuleStatus? EvaluatePath(StatusProbe probe)
    {
        if (string.IsNullOrWhiteSpace(probe.Path)) return null;

        var path = Environment.ExpandEnvironmentVariables(probe.Path);
        var hit = File.Exists(path) || Directory.Exists(path);

        return new ModuleStatus(
            hit ? StatusLevel.Good : StatusLevel.Warn,
            hit ? probe.WhenTrue ?? "set up" : probe.WhenFalse ?? "not set up",
            DateTimeOffset.Now,
            path);
    }

    private static ModuleStatus? EvaluateCount(StatusProbe probe)
    {
        if (string.IsNullOrWhiteSpace(probe.Path)) return null;

        var path = Environment.ExpandEnvironmentVariables(probe.Path);
        if (!Directory.Exists(path))
            return new ModuleStatus(StatusLevel.Neutral, probe.WhenFalse ?? "nothing yet", DateTimeOffset.Now, path);

        // EnumerateFiles: count without building an array of all names.
        var count = Directory.EnumerateFiles(path, probe.Pattern ?? "*", SearchOption.TopDirectoryOnly).Count();

        var template = count > 0 ? probe.WhenTrue ?? "{n} files" : probe.WhenFalse ?? "nothing yet";
        return new ModuleStatus(
            count > 0 ? StatusLevel.Good : StatusLevel.Neutral,
            template.Replace("{n}", count.ToString()),
            DateTimeOffset.Now,
            path);
    }

    private static ModuleStatus? EvaluateTools(ModuleManifest manifest)
    {
        var missing = manifest.Requires
            .Where(r => r.Mandatory
                && ExternalTool.Find(r.Tool, manifest.Directory, AppPaths.ModuleData(manifest.Id)) is null)
            .Select(r => r.Tool)
            .ToList();

        if (missing.Count == 0)
            return new ModuleStatus(StatusLevel.Good, manifest.Status.WhenTrue ?? "ready", DateTimeOffset.Now);

        return new ModuleStatus(
            StatusLevel.Warn,
            manifest.Status.WhenFalse ?? $"{missing[0]} missing",
            DateTimeOffset.Now,
            "Not found: " + string.Join(", ", missing));
    }
}

/// <summary>Finds an external program without starting it.</summary>
public static class ExternalTool
{
    /// <summary>The module side of it. See <see cref="MultiTool.Abstractions.IToolLocator"/>.</summary>
    internal sealed class Locator(string moduleDirectory, Func<string> dataDirectory)
        : MultiTool.Abstractions.IToolLocator
    {
        public string? Find(string tool) => ExternalTool.Find(tool, moduleDirectory, dataDirectory());

        public string? FindWritable(string tool)
        {
            var found = Find(tool);
            if (found is null) return null;

            // Already in a writable place: nothing to do.
            if (!AppPaths.IsInsideAppDirectory(found)) return found;

            try
            {
                var target = Path.Combine(AppPaths.EnsureDirectory(Path.Combine(dataDirectory(), "tools")),
                    Path.GetFileName(found));

                // Copy only once. Afterwards the copy belongs to the program itself; overwriting would undo
                // the very update the copy exists for.
                if (!File.Exists(target)) File.Copy(found, target);

                return target;
            }
            catch (Exception)
            {
                // Without a writable copy the bundled one still works; it just can't update itself.
                return found;
            }
        }
    }

    /// <summary>Looks in the module data folder, the module folder, the app folder, then <c>PATH</c>.</summary>
    /// <remarks>
    /// The data folder comes first because that is where shell downloads land, the version the user
    /// chose. <c>PATH</c> comes last so behavior doesn't depend on whatever else is installed.
    /// </remarks>
    public static string? Find(string tool, string? moduleDirectory = null, string? dataDirectory = null)
    {
        var exe = tool.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? tool : tool + ".exe";

        if (dataDirectory is not null)
        {
            foreach (var candidate in new[] { Path.Combine(dataDirectory, exe),
                                              Path.Combine(dataDirectory, "tools", exe) })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (moduleDirectory is not null)
        {
            var local = Path.Combine(moduleDirectory, exe);
            if (File.Exists(local)) return local;
        }

        var app = Path.Combine(AppPaths.AppDirectory, "tools", exe);
        if (File.Exists(app)) return app;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid characters is no reason to stop.
            }
        }

        return null;
    }
}
