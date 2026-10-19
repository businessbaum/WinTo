namespace MultiTool.Abstractions;

/// <summary>Everything a module gets from the host.</summary>
public interface IModuleContext
{
    ModuleManifest Manifest { get; }

    /// <summary>
    /// Folder of the module DLL. Under <c>Program Files</c> in a real install and
    /// <b>read-only</b>; never write here.
    /// </summary>
    string ModuleDirectory { get; }

    /// <summary>
    /// <c>%APPDATA%\WinTo\modules\&lt;id&gt;</c>, the only place a module may write to.
    /// Created on first access.
    /// </summary>
    string DataDirectory { get; }

    /// <summary>The module's own settings as JSON, managed by the host.</summary>
    IModuleSettings Settings { get; }

    /// <summary>Dialogs, file pickers, notices, elevation: everything the shell can do.</summary>
    IShell Shell { get; }

    /// <summary>Finds external programs such as <c>ffmpeg</c> or <c>yt-dlp</c>.</summary>
    IToolLocator Tools { get; }

    IModuleLog Log { get; }

    /// <summary>
    /// Reports the tile status. Written to disk so the home page can show it on the next start
    /// without loading the module. Only applies if the manifest says <see cref="StatusProbeKind.Cached"/>.
    /// </summary>
    void PublishStatus(ModuleStatus status);

    /// <summary>Cancelled when the app quits. Stop running work here.</summary>
    CancellationToken Shutdown { get; }
}

/// <summary>A module's settings, stored as <c>settings.json</c> in its data folder.</summary>
/// <remarks>Reflection-based; allowed because the app is never trimmed.</remarks>
public interface IModuleSettings
{
    /// <summary>Loads the settings. Missing or broken file: a fresh <typeparamref name="T"/>.</summary>
    T Load<T>() where T : class, new();

    /// <summary>
    /// Writes the settings via a temp file and rename; the last good version stays as <c>.bak</c>.
    /// </summary>
    /// <remarks>
    /// Since 0.9.0 deferred and exception-free: serialized immediately, written on a background
    /// thread once no new value arrived for about half a second. Everything is on disk at the
    /// latest when the module is released or the app quits; <see cref="Load{T}"/> already sees
    /// the pending value. Failures go to the module log.
    /// </remarks>
    void Save<T>(T value) where T : class;
}

/// <summary>Finds external programs without starting them.</summary>
public interface IToolLocator
{
    /// <summary>
    /// Looks in the module data folder (where the shell downloads tools), the module folder,
    /// <c>&lt;app&gt;\tools</c>, then <c>PATH</c>. Returns <c>null</c> if nothing was found.
    /// </summary>
    string? Find(string tool);

    /// <summary>The same, but in a writable location.</summary>
    /// <remarks>
    /// For self-updating tools such as <c>yt-dlp</c>: its updater fails under the read-only
    /// <c>Program Files</c>, so the bundled copy is copied to the module data folder on first use.
    /// </remarks>
    string? FindWritable(string tool);
}

public interface IModuleLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
