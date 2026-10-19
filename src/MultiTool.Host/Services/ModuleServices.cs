using MultiTool.Abstractions;

namespace MultiTool.Host.Services;

/// <summary>A module's settings as <c>settings.json</c> in its data folder.</summary>
/// <remarks>
/// <see cref="Save{T}"/> never throws and writes deferred (see <see cref="DeferredJson"/>). Modules
/// call it from property callbacks, where an exception used to end the app.
/// </remarks>
internal sealed class ModuleSettings(string dataDirectory, IModuleLog log) : IModuleSettings
{
    private string File => Path.Combine(dataDirectory, "settings.json");

    public T Load<T>() where T : class, new() => JsonStore.Load(File, static () => new T());

    public void Save<T>(T value) where T : class
    {
        string json;
        try
        {
            json = JsonStore.Serialize(value);
        }
        catch (Exception ex)
        {
            log.Error("Settings could not be serialized.", ex);
            return;
        }

        DeferredJson.Write(File, json, ex => log.Error("Settings could not be written.", ex));
    }

    /// <summary>Writes pending changes now. Called when the module is released.</summary>
    public void Flush() => DeferredJson.Flush(File);
}

/// <summary>One log per module under <c>%APPDATA%\WinTo\logs</c>.</summary>
/// <remarks>
/// Separate files because the first question is always "which module". Rotated once at 1 MB.
/// </remarks>
internal sealed class ModuleLog(string moduleId) : IModuleLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly System.Text.UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>One lock per file, not per instance.</summary>
    /// <remarks>
    /// Every module open creates a new log object; an old instance's background thread could still
    /// be writing, and two locks on one file would lose a line to a sharing violation.
    /// </remarks>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lock> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = Gates.GetOrAdd(moduleId, static _ => new Lock());

    private string File => Path.Combine(AppPaths.LogDirectory, $"{moduleId}.log");

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("WARN ", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                AppPaths.EnsureDirectory(AppPaths.LogDirectory);
                var path = File;

                if (System.IO.File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    System.IO.File.Move(path, path + ".1", overwrite: true);

                // With BOM, otherwise PowerShell 5.1 reads the file as ANSI and mangles non-ASCII text.
                // .NET does not write it again when appending.
                System.IO.File.AppendAllText(path,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {level} {message}{Environment.NewLine}",
                    Utf8WithBom);
            }
        }
        catch (Exception)
        {
            // A failing log must never become the problem it reports.
        }
    }
}

/// <summary>Cached tile states.</summary>
/// <remarks>
/// A module knows its status only while running; the home page needs it without loading the
/// module. The module writes it here, and the home page shows it with its age.
/// </remarks>
internal static class StatusStore
{
    private static string FileFor(string moduleId) =>
        Path.Combine(AppPaths.StatusDirectory, $"{moduleId}.json");

    public static ModuleStatus? Read(string moduleId)
    {
        var stored = JsonStore.Load<StoredStatus>(FileFor(moduleId), static () => null!);
        return stored is null ? null
            : new ModuleStatus(stored.Level, stored.Text, stored.AsOf, stored.Detail);
    }

    public static void Write(string moduleId, ModuleStatus status)
    {
        try
        {
            AppPaths.EnsureDirectory(AppPaths.StatusDirectory);
            JsonStore.Save(FileFor(moduleId), new StoredStatus
            {
                Level = status.Level,
                Text = status.Text,
                AsOf = status.AsOf ?? DateTimeOffset.Now,
                Detail = status.Detail,
            });
        }
        catch (Exception)
        {
            // A status that wasn't written costs one tile line, nothing more.
        }
    }

    private sealed class StoredStatus
    {
        public StatusLevel Level { get; set; }
        public string Text { get; set; } = "";
        public DateTimeOffset AsOf { get; set; }
        public string? Detail { get; set; }
    }
}
