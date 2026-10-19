using System.Diagnostics;
using Avalonia.Controls;
using MultiTool.Abstractions;

namespace MultiTool.Host.Services;

/// <summary>Loads and releases modules.</summary>
/// <remarks>
/// <para>
/// In one sentence: <b>before a module is first opened, its assembly is not in the process.</b>
/// </para>
/// <para>
/// Releasing is not unloading. A module's load context stays for the process lifetime (see
/// <see cref="ModuleLoadContext"/>); the instance is released with its devices, threads, buffers
/// and files. Side effect: the second open of a module is almost free.
/// </para>
/// </remarks>
public sealed class ModuleManager(IShell shell, CancellationToken shutdown)
{
    private readonly Dictionary<string, ModuleSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleHistory> _history = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Modules with a live instance.</summary>
    public IReadOnlyCollection<ModuleSession> Loaded => _sessions.Values;

    /// <summary>Modules whose code is loaded, including released ones. Feeds the diagnostics page.</summary>
    public IReadOnlyCollection<ModuleHistory> History => _history.Values;

    public ModuleSession? Find(string moduleId) =>
        _sessions.TryGetValue(moduleId, out var s) ? s : null;

    /// <summary>Loads the module if needed and returns its session; the same one on a second call.</summary>
    /// <exception cref="ModuleLoadException">The module could not be loaded.</exception>
    public ModuleSession Load(ModuleManifest manifest)
    {
        if (_sessions.TryGetValue(manifest.Id, out var existing)) return existing;

        var stopwatch = Stopwatch.StartNew();

        // On a second open the context still exists; only the instance is created.
        var firstLoad = !_contexts.TryGetValue(manifest.Id, out var context);

        try
        {
            // Inside the try: the AssemblyDependencyResolver throws InvalidOperationException when the
            // module's .deps.json is missing or mismatched. Outside it, that took down the whole app.
            // A broken module folder may cost a tile, never the shell.
            context ??= new ModuleLoadContext(manifest);

            // Remember right away: the context can't be unloaded, and a failure afterwards would orphan
            // it while the next click creates another one.
            _contexts[manifest.Id] = context;

            // LoadFromAssemblyPath: the module folder is on no probing path, on purpose.
            var assembly = context.LoadFromAssemblyPath(manifest.AssemblyPath);

            var type = assembly.GetType(manifest.EntryType, throwOnError: false)
                ?? throw new ModuleLoadException(
                    "The type named in the manifest is not in the assembly: " + manifest.EntryType);

            if (!typeof(IModule).IsAssignableFrom(type))
                throw new ModuleLoadException(
                    manifest.EntryType + " does not implement IModule. Usually caused by a private " +
                    "copy of MultiTool.Abstractions or Avalonia in the module folder: the reference needs " +
                    "Private=false and ExcludeAssets=runtime.");

            if (Activator.CreateInstance(type) is not IModule module)
                throw new ModuleLoadException(manifest.EntryType + " could not be created.");

            var moduleContext = new ModuleContext(manifest, shell, shutdown);

            try
            {
                module.Initialize(moduleContext);
            }
            catch (Exception)
            {
                // Whatever Initialize created before failing (a device, a thread, a hook) still has to go
                // back; the load context is never collected.
                try { module.Dispose(); } catch (Exception) { }
                throw;
            }

            stopwatch.Stop();
            moduleContext.Log.Info(
                firstLoad
                    ? $"Loaded in {stopwatch.ElapsedMilliseconds} ms (new load context)."
                    : $"Reopened in {stopwatch.ElapsedMilliseconds} ms (load context existed).");

            var history = _history.TryGetValue(manifest.Id, out var h)
                ? h
                : _history[manifest.Id] = new ModuleHistory(manifest.Id, manifest.Name);
            history.Record(stopwatch.Elapsed, firstLoad);

            var session = new ModuleSession(manifest, module, moduleContext, stopwatch.Elapsed, firstLoad);
            _sessions[manifest.Id] = session;
            return session;
        }
        catch (Exception ex)
        {
            // The context stays in _contexts on purpose so a retry reuses it. Rebuilding the module DLL
            // after a failed attempt needs a restart, as after any successful load.
            if (ex is ModuleLoadException) throw;

            // The resolver says "Failed to locate managed application" and means the missing
            // .deps.json next to the DLL.
            if (ex is InvalidOperationException && ex.Message.Contains("Dependency resolution failed"))
            {
                throw new ModuleLoadException(
                    $"The module folder of \"{manifest.Name}\" is incomplete: the matching .deps.json is missing "
                    + $"next to the DLL.{Environment.NewLine}{manifest.Directory}"
                    + $"{Environment.NewLine}{Environment.NewLine}A new build restores it.",
                    ex);
            }

            throw new ModuleLoadException(
                $"The module \"{manifest.Name}\" could not be loaded: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Releases the module instance. If it is working and <paramref name="force"/> is not set,
    /// nothing happens and <c>false</c> is returned.
    /// </summary>
    public bool Release(string moduleId, bool force = false)
    {
        if (!_sessions.TryGetValue(moduleId, out var session)) return true;

        if (!force && session.Module.Activity.IsBusy) return false;

        _sessions.Remove(moduleId);

        try
        {
            session.Module.Dispose();
        }
        catch (Exception)
        {
            // A module throwing during cleanup must not take the app with it.
        }

        // After Dispose: many modules save one last time there.
        session.Context.FlushSettings();

        if (_history.TryGetValue(moduleId, out var history)) history.Released();
        return true;
    }
}

/// <summary>A loaded module.</summary>
public sealed class ModuleSession(
    ModuleManifest manifest, IModule module, ModuleContext context, TimeSpan loadTime, bool firstLoad)
{
    public ModuleManifest Manifest { get; } = manifest;

    public IModule Module { get; } = module;

    internal ModuleContext Context { get; } = context;

    /// <summary>How long loading took. Shown on the diagnostics page.</summary>
    public TimeSpan LoadTime { get; } = loadTime;

    /// <summary>Whether a new load context had to be created.</summary>
    public bool FirstLoad { get; } = firstLoad;

    /// <summary>The currently open view, if any.</summary>
    public Control? View { get; internal set; }
}

/// <summary>What happened to a module since startup.</summary>
/// <remarks>
/// Separate from <see cref="ModuleSession"/> because a session ends and the load context stays,
/// which is exactly what the diagnostics page shows.
/// </remarks>
public sealed class ModuleHistory(string moduleId, string name)
{
    public string ModuleId { get; } = moduleId;

    public string Name { get; } = name;

    /// <summary>How often the module was opened.</summary>
    public int Opens { get; private set; }

    /// <summary>Duration of the very first load, including the load context.</summary>
    public TimeSpan FirstLoadTime { get; private set; }

    /// <summary>Duration of the last open.</summary>
    public TimeSpan LastLoadTime { get; private set; }

    /// <summary>Is an instance alive?</summary>
    public bool Live { get; private set; }

    internal void Record(TimeSpan elapsed, bool firstLoad)
    {
        Opens++;
        LastLoadTime = elapsed;
        if (firstLoad) FirstLoadTime = elapsed;
        Live = true;
    }

    internal void Released() => Live = false;
}

public sealed class ModuleLoadException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The host side of the module contract.</summary>
public sealed class ModuleContext : IModuleContext
{
    private readonly Lazy<string> _dataDirectory;

    public ModuleContext(ModuleManifest manifest, IShell shell, CancellationToken shutdown)
    {
        Manifest = manifest;
        Shell = shell;
        Shutdown = shutdown;
        Log = new ModuleLog(manifest.Id);
        _settings = new ModuleSettings(AppPaths.ModuleData(manifest.Id), Log);
        Tools = new ExternalTool.Locator(manifest.Directory, () => DataDirectory);

        // Created on first write, so a module that never saves leaves no empty folder.
        _dataDirectory = new Lazy<string>(
            () => AppPaths.EnsureDirectory(AppPaths.ModuleData(manifest.Id)));
    }

    public ModuleManifest Manifest { get; }

    public string ModuleDirectory => Manifest.Directory;

    public string DataDirectory => _dataDirectory.Value;

    private readonly ModuleSettings _settings;

    public IModuleSettings Settings => _settings;

    /// <summary>Writes deferred settings. See <see cref="DeferredJson"/>.</summary>
    public void FlushSettings() => _settings.Flush();

    public IShell Shell { get; }

    public IToolLocator Tools { get; }

    public IModuleLog Log { get; }

    public CancellationToken Shutdown { get; }

    public void PublishStatus(ModuleStatus status) => StatusStore.Write(Manifest.Id, status);
}
