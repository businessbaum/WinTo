using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace MultiTool.Modules.SystemOverview;

/// <summary>A change to an environment variable, so it can be undone.</summary>
/// <param name="OldValue">The raw value before, <c>null</c> if the variable didn't exist.</param>
/// <param name="OldKind">The kind before, <c>REG_EXPAND_SZ</c> or <c>REG_SZ</c>.</param>
/// <param name="What">What happened, for the tooltip: "saved", "created", "deleted".</param>
public sealed record VariableChange(
    DateTimeOffset When,
    string Name,
    bool Machine,
    string? OldValue,
    RegistryValueKind OldKind,
    string What);

/// <summary>The latest changes to environment variables, with the value before.</summary>
/// <remarks>
/// <para>
/// Until 0.9.0 there was no way back: this section wrote <c>PATH</c> straight to the registry, and a wrongly
/// removed entry could only be restored from memory.
/// </para>
/// <para>
/// A separate file in the data folder, not the module's <c>settings.json</c>, so later settings can't
/// overwrite it. It survives closing (a missing PATH entry is often noticed the next day). Fifty
/// entries, the oldest drop out.
/// </para>
/// </remarks>
internal sealed class VariableHistory
{
    private const int Limit = 50;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _file;
    private readonly List<VariableChange> _changes;

    public VariableHistory(string dataDirectory)
    {
        _file = Path.Combine(dataDirectory, "variable-history.json");
        _changes = Read(_file);
    }

    public VariableChange? Last => _changes.Count > 0 ? _changes[^1] : null;

    public void Record(VariableChange change)
    {
        _changes.Add(change);
        if (_changes.Count > Limit) _changes.RemoveRange(0, _changes.Count - Limit);
        Persist();
    }

    public void DropLast()
    {
        if (_changes.Count == 0) return;
        _changes.RemoveAt(_changes.Count - 1);
        Persist();
    }

    private static List<VariableChange> Read(string file)
    {
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<List<VariableChange>>(File.ReadAllText(file), Options) ?? []
                : [];
        }
        catch (Exception)
        {
            // A broken history costs undo, not the module.
            return [];
        }
    }

    /// <summary>Via temp file and rename, like everywhere something valuable is stored.</summary>
    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temporary = _file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_changes, Options));
            File.Move(temporary, _file, overwrite: true);
        }
        catch (Exception)
        {
            // The in-memory history stays valid until the module closes.
        }
    }
}
