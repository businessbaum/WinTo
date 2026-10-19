using System.Diagnostics;
using Microsoft.Win32;

namespace MultiTool.Modules.SystemOverview;

/// <summary>Where an autostart entry lives. Decides rights, display and how to switch it off.</summary>
public enum AutostartLocation
{
    UserRun,
    MachineRun,

    /// <summary>32-bit programs on 64-bit Windows: <c>Wow6432Node</c>.</summary>
    MachineRun32,

    UserRunOnce,
    MachineRunOnce,
    UserStartupFolder,
    CommonStartupFolder,
}

/// <param name="Name">The value name in the key, or the shortcut's file name.</param>
/// <param name="Command">The full command line as stored.</param>
/// <param name="TargetPath">The program called, extracted from <paramref name="Command"/>.</param>
/// <param name="Publisher">Company name from the file's version info.</param>
public sealed record AutostartEntry(
    string Name,
    string Command,
    string? TargetPath,
    string? Publisher,
    AutostartLocation Location,
    bool Enabled)
{
    /// <summary>Whether changing this entry needs administrator rights.</summary>
    public bool NeedsElevation => Location
        is AutostartLocation.MachineRun
        or AutostartLocation.MachineRun32
        or AutostartLocation.MachineRunOnce
        or AutostartLocation.CommonStartupFolder;

    /// <summary>Whether this entry has an off state.</summary>
    /// <remarks><c>RunOnce</c> deletes itself when run and has no approval list; a switch that does nothing would be worse than none.</remarks>
    public bool CanToggle => Location
        is not (AutostartLocation.UserRunOnce or AutostartLocation.MachineRunOnce);

    public string LocationText => Location switch
    {
        AutostartLocation.UserRun => "User",
        AutostartLocation.MachineRun => "All users",
        AutostartLocation.MachineRun32 => "All users (32-bit)",
        AutostartLocation.UserRunOnce => "User, once",
        AutostartLocation.MachineRunOnce => "All users, once",
        AutostartLocation.UserStartupFolder => "Startup folder",
        AutostartLocation.CommonStartupFolder => "Startup folder (all)",
        _ => "",
    };
}

/// <summary>Reads and toggles the autostart list, the same way Task Manager does.</summary>
/// <remarks>
/// <para>
/// <b>Disabling doesn't delete.</b> Besides the <c>Run</c> keys, Windows keeps a second list under
/// <c>Explorer\StartupApproved</c> with twelve bytes per entry: the first says on or off, the last eight
/// are when it was disabled. Task Manager writes exactly this list; deleting the Run value instead would
/// remove the entry for good.
/// </para>
/// <para>
/// The encoding is undocumented but clear: <c>02 00 …</c> on, <c>03 00 …</c> off. Only the lowest bit is
/// evaluated, so <c>06</c>/<c>07</c> seen in the wild read correctly too.
/// </para>
/// </remarks>
internal static class Autostart
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32Path = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Reads all entries. On a background thread: file metadata for twenty targets takes half a second.</summary>
    public static List<AutostartEntry> Read()
    {
        var entries = new List<AutostartEntry>();

        ReadRegistry(entries, Registry.CurrentUser, RunPath, AutostartLocation.UserRun);
        ReadRegistry(entries, Registry.LocalMachine, RunPath, AutostartLocation.MachineRun);
        ReadRegistry(entries, Registry.LocalMachine, Run32Path, AutostartLocation.MachineRun32);
        ReadRegistry(entries, Registry.CurrentUser, RunOncePath, AutostartLocation.UserRunOnce);
        ReadRegistry(entries, Registry.LocalMachine, RunOncePath, AutostartLocation.MachineRunOnce);

        ReadFolder(entries, Environment.SpecialFolder.Startup, AutostartLocation.UserStartupFolder);
        ReadFolder(entries, Environment.SpecialFolder.CommonStartup, AutostartLocation.CommonStartupFolder);

        return [.. entries
            .OrderByDescending(e => e.Enabled)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Switches an entry on or off.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? SetEnabled(AutostartEntry entry, bool enabled)
    {
        if (!entry.CanToggle) return "One-time entries can't be disabled.";

        var (root, subKey, valueName) = ApprovalTarget(entry);

        try
        {
            using var key = root.CreateSubKey(subKey, writable: true)
                ?? throw new IOException("Approval key could not be created.");

            key.SetValue(valueName, ApprovalBytes(enabled), RegistryValueKind.Binary);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return entry.NeedsElevation
                ? "Entry for all users: administrator rights required."
                : "Access to the registry denied.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Removes an entry for good: registry value or shortcut.</summary>
    /// <returns><c>null</c> on success, otherwise the reason.</returns>
    public static string? Remove(AutostartEntry entry)
    {
        try
        {
            if (entry.Location is AutostartLocation.UserStartupFolder
                              or AutostartLocation.CommonStartupFolder)
            {
                var folder = Environment.GetFolderPath(
                    entry.Location == AutostartLocation.UserStartupFolder
                        ? Environment.SpecialFolder.Startup
                        : Environment.SpecialFolder.CommonStartup);

                File.Delete(Path.Combine(folder, entry.Name));
            }
            else
            {
                var (root, path) = RegistryTarget(entry);
                using var key = root.OpenSubKey(path, writable: true);
                key?.DeleteValue(entry.Name, throwOnMissingValue: false);
            }

            // The orphaned approval entry does no harm, but it would mark a later entry of the same name as off.
            var (approvalRoot, approvalKey, approvalName) = ApprovalTarget(entry);
            using var approval = approvalRoot.OpenSubKey(approvalKey, writable: true);
            approval?.DeleteValue(approvalName, throwOnMissingValue: false);

            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return entry.NeedsElevation
                ? "Entry for all users: administrator rights required."
                : "Access denied.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------------------------------------------------------------- read

    private static void ReadRegistry(
        List<AutostartEntry> into, RegistryKey root, string path, AutostartLocation location)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;

            // RunOnce has no approval list; Windows always runs those. Looking into StartupApproved\Run
            // would hit a Run entry of the same name and show its off state.
            var runOnce = location
                is AutostartLocation.UserRunOnce or AutostartLocation.MachineRunOnce;

            using var approval = runOnce
                ? null
                : root.OpenSubKey($@"{ApprovedPath}\{ApprovalLeaf(location)}");

            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is not string command || command.Length == 0) continue;

                var target = ResolveTarget(command);

                into.Add(new AutostartEntry(
                    Name: name,
                    Command: command,
                    TargetPath: target,
                    Publisher: PublisherOf(target),
                    Location: location,
                    Enabled: IsApproved(approval, name)));
            }
        }
        catch (Exception)
        {
            // An unreadable branch costs its entries. Everything else still shows.
        }
    }

    private static void ReadFolder(
        List<AutostartEntry> into, Environment.SpecialFolder folder, AutostartLocation location)
    {
        try
        {
            var path = Environment.GetFolderPath(folder);
            if (path.Length == 0 || !Directory.Exists(path)) return;

            var root = location == AutostartLocation.UserStartupFolder
                ? Registry.CurrentUser
                : Registry.LocalMachine;

            using var approval = root.OpenSubKey($@"{ApprovedPath}\StartupFolder");

            foreach (var file in Directory.EnumerateFiles(path))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                // Getting a shortcut's target would mean binding IShellLink over COM. For display the
                // shortcut's own path is enough; its name is what the user sees in the folder anyway.
                into.Add(new AutostartEntry(
                    Name: name,
                    Command: file,
                    TargetPath: file,
                    Publisher: null,
                    Location: location,
                    Enabled: IsApproved(approval, name)));
            }
        }
        catch (Exception)
        {
            // as above
        }
    }

    private static bool IsApproved(RegistryKey? approval, string name)
    {
        // No entry in the approval list means "never disabled", i.e. on.
        if (approval?.GetValue(name) is not byte[] { Length: > 0 } data) return true;

        return (data[0] & 1) == 0;
    }

    // ---------------------------------------------------------------- write

    private static byte[] ApprovalBytes(bool enabled)
    {
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;

        // When disabling, Windows writes the time into the last eight bytes. Nothing here reads it,
        // but Task Manager shows it, and a date of 1601 would look like an error there.
        if (!enabled)
            BitConverter.TryWriteBytes(data.AsSpan(4), DateTime.UtcNow.ToFileTimeUtc());

        return data;
    }

    private static (RegistryKey Root, string Path) RegistryTarget(AutostartEntry entry) => entry.Location switch
    {
        AutostartLocation.UserRun => (Registry.CurrentUser, RunPath),
        AutostartLocation.MachineRun => (Registry.LocalMachine, RunPath),
        AutostartLocation.MachineRun32 => (Registry.LocalMachine, Run32Path),
        AutostartLocation.UserRunOnce => (Registry.CurrentUser, RunOncePath),
        AutostartLocation.MachineRunOnce => (Registry.LocalMachine, RunOncePath),
        _ => (Registry.CurrentUser, RunPath),
    };

    private static (RegistryKey Root, string Path, string Value) ApprovalTarget(AutostartEntry entry)
    {
        var root = entry.NeedsElevation ? Registry.LocalMachine : Registry.CurrentUser;
        return (root, $@"{ApprovedPath}\{ApprovalLeaf(entry.Location)}", entry.Name);
    }

    private static string ApprovalLeaf(AutostartLocation location) => location switch
    {
        AutostartLocation.MachineRun32 => "Run32",
        AutostartLocation.UserStartupFolder or AutostartLocation.CommonStartupFolder => "StartupFolder",
        _ => "Run",
    };

    // ---------------------------------------------------------------- command line

    /// <summary>Extracts the called program from a command line.</summary>
    /// <remarks>
    /// The easy case is quoted. The unpleasant one isn't: for <c>C:\Program Files\Foo\bar.exe -x</c> the end of
    /// the path can't be decided without looking at the disk, so every prefix is checked against the file
    /// system from the left, the rule Windows itself follows.
    /// </remarks>
    private static string? ResolveTarget(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? Environment.ExpandEnvironmentVariables(text[1..end]) : null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(text);

        // The whole text first: a command line without arguments is the common case.
        if (File.Exists(expanded)) return expanded;

        for (var space = expanded.IndexOf(' '); space > 0; space = expanded.IndexOf(' ', space + 1))
        {
            var candidate = expanded[..space];
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? PublisherOf(string? path)
    {
        if (path is null || !File.Exists(path)) return null;

        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim();
            return company is null or "" ? null : company;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
