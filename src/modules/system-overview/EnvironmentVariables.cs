using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MultiTool.Modules.SystemOverview;

/// <summary>An entry of a list-like variable, one line of <c>Path</c>.</summary>
/// <param name="Text">As written, unexpanded: <c>%USERPROFILE%\bin</c>.</param>
/// <param name="Expanded">The same entry with percent variables expanded.</param>
/// <param name="LooksLikePath">A folder, as opposed to <c>.COM</c> from <c>PATHEXT</c>.</param>
public sealed record ListEntry(
    string Text,
    string Expanded,
    bool LooksLikePath,
    bool Exists,
    bool IsDuplicate)
{
    public bool IsProblem => IsDuplicate || (LooksLikePath && !Exists);

    public string ProblemText => IsDuplicate ? "duplicate"
        : LooksLikePath && !Exists ? "missing"
        : "";

    /// <summary>Only shown when written and meant value differ.</summary>
    public bool ShowExpanded => !string.Equals(Text, Expanded, StringComparison.Ordinal);
}

/// <summary>An environment variable as stored in the registry.</summary>
/// <param name="RawValue">
/// <b>Unexpanded.</b> The difference between <c>%USERPROFILE%\bin</c> and its expanded path is the whole
/// reason the registry is read instead of <c>Environment.GetEnvironmentVariables</c>.
/// </param>
public sealed record EnvVariable(string Name, string RawValue, bool Machine, RegistryValueKind Kind)
{
    public string ScopeText => Machine ? "System" : "User";

    /// <summary>Whether the value is read as a list.</summary>
    /// <remarks>
    /// By semicolon, not by name: besides <c>Path</c> and <c>PATHEXT</c> there are <c>PSModulePath</c>,
    /// <c>CLASSPATH</c> and whatever installers add.
    /// </remarks>
    public bool IsList => RawValue.Contains(';');
}

/// <summary>Reads and writes environment variables.</summary>
/// <remarks>
/// <para>
/// <b>Via the registry, not <see cref="Environment"/>:</b> <c>GetEnvironmentVariable(…, Machine)</c> expands
/// <c>REG_EXPAND_SZ</c>, turning <c>%SystemRoot%\system32</c> into <c>C:\Windows\system32</c>. Annoying to
/// display, damaging to write back: the variable no longer follows the system folder.
/// <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/> prevents that.
/// </para>
/// <para>
/// Named <c>EnvironmentVariables</c>, not <c>Environment</c>: a type of that name in the module namespace
/// would shadow <see cref="System.Environment"/> and break every <c>Environment.GetFolderPath</c> call.
/// </para>
/// </remarks>
internal static partial class EnvironmentVariables
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserKey = "Environment";

    public static List<EnvVariable> Read()
    {
        var result = new List<EnvVariable>();

        ReadInto(result, machine: false);
        ReadInto(result, machine: true);

        return [.. result
            .OrderBy(v => v.Machine)
            .ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static void ReadInto(List<EnvVariable> target, bool machine)
    {
        try
        {
            using var key = machine
                ? Registry.LocalMachine.OpenSubKey(MachineKey)
                : Registry.CurrentUser.OpenSubKey(UserKey);

            if (key is null) return;

            foreach (var name in key.GetValueNames())
            {
                var kind = key.GetValueKind(name);
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;

                var value = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                target.Add(new EnvVariable(name, value, machine, kind));
            }
        }
        catch (Exception)
        {
            // A branch that can't be opened yields no variables. Reading needs no rights; if it fails
            // anyway, an empty half is the right answer, not a crash.
        }
    }

    /// <summary>Splits a list value and judges each entry.</summary>
    /// <remarks>
    /// Duplicates compare the <b>expanded</b> path without trailing separator; otherwise <c>C:\Tools</c> and
    /// <c>C:\Tools\</c> would differ although Windows treats them alike, which is how duplicates arise.
    /// </remarks>
    public static List<ListEntry> Split(string rawValue)
    {
        var result = new List<ListEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in rawValue.Split(';'))
        {
            var text = part.Trim();
            if (text.Length == 0) continue;

            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(text); }
            catch (Exception) { expanded = text; }

            var looksLikePath = expanded.Contains('\\');
            var duplicate = !seen.Add(expanded.TrimEnd('\\', '/'));

            var exists = false;
            if (looksLikePath)
            {
                // A network drive that is currently absent throws here and is not a dead entry yet. When in
                // doubt it counts as present: a path wrongly marked dead invites deleting something needed.
                try { exists = Directory.Exists(expanded); }
                catch (Exception) { exists = true; }
            }

            result.Add(new ListEntry(text, expanded, looksLikePath, exists, duplicate));
        }

        return result;
    }

    /// <summary>Writes a value. <c>null</c> on success, otherwise the reason.</summary>
    public static string? Write(EnvVariable variable, string newValue)
    {
        try
        {
            using var key = variable.Machine
                ? Registry.LocalMachine.OpenSubKey(MachineKey, writable: true)
                : Registry.CurrentUser.OpenSubKey(UserKey, writable: true);

            if (key is null) return "Registry branch could not be opened.";

            // The kind follows the content, not what was there: adding %USERPROFILE% to a REG_SZ means
            // it should expand. A REG_EXPAND_SZ without percent signs stays one; that does no harm.
            var kind = newValue.Contains('%') ? RegistryValueKind.ExpandString : variable.Kind;

            key.SetValue(variable.Name, newValue, kind);
            Broadcast();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return variable.Machine
                ? "Changing system variables requires administrator rights."
                : "Access denied.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Creates a new variable. <c>null</c> on success, otherwise the reason.</summary>
    public static string? Create(string name, string value, bool machine)
    {
        var trimmed = name.Trim();

        if (trimmed.Length == 0) return "Name missing.";
        if (trimmed.Contains('=')) return "Equals sign not allowed in the name.";

        return Write(new EnvVariable(trimmed, "", machine, RegistryValueKind.String), value);
    }

    /// <summary>Deletes a variable. <c>null</c> on success, otherwise the reason.</summary>
    public static string? Remove(EnvVariable variable)
    {
        try
        {
            using var key = variable.Machine
                ? Registry.LocalMachine.OpenSubKey(MachineKey, writable: true)
                : Registry.CurrentUser.OpenSubKey(UserKey, writable: true);

            if (key is null) return "Registry branch could not be opened.";

            key.DeleteValue(variable.Name, throwOnMissingValue: false);
            Broadcast();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return variable.Machine
                ? "Changing system variables requires administrator rights."
                : "Access denied.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ------------------------------------------------------------------ Notifying

    private const int HwndBroadcast = 0xFFFF;
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint SendMessageTimeout(
        nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);

    /// <summary>Tells all windows that the environment changed.</summary>
    /// <remarks>
    /// <para>
    /// <b>Without this nothing visible happens:</b> a process inherits its environment at start and never
    /// rereads it. This broadcast makes Explorer refresh its copy, and with it everything started from
    /// there.
    /// </para>
    /// <para>
    /// With timeout and <c>SMTO_ABORTIFHUNG</c>, not <c>SendMessage</c>: it goes to <b>every</b> window, and a
    /// single hung program would stall the app.
    /// </para>
    /// <para>It does <b>not</b> update consoles already running; the view says so.</para>
    /// </remarks>
    public static void Broadcast()
    {
        // Fire and forget on a background thread: one sluggish program could hold the caller (a
        // button command on the UI thread) for up to a second. The value is already written.
        _ = Task.Run(() =>
        {
            try
            {
                SendMessageTimeout(HwndBroadcast, WmSettingChange, 0, "Environment",
                    SmtoAbortIfHung, 1000, out _);
            }
            catch (Exception)
            {
                // The broadcast is a courtesy to running programs; the value is written without it.
            }
        });
    }
}
