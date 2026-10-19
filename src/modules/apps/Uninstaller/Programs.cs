using System.Diagnostics;
using Microsoft.Win32;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Apps.Uninstaller;

/// <summary>Where an entry lives. Decides whether removal needs rights.</summary>
public enum ProgramScope
{
    /// <summary>Installed for this user only.</summary>
    User,

    /// <summary>For all users, 64-bit.</summary>
    Machine,

    /// <summary>For all users, 32-bit on 64-bit Windows.</summary>
    Machine32,
}

public sealed record InstalledProgram(
    string Key,
    string Name,
    string? Publisher,
    string? Version,
    DateTime? InstalledOn,
    long SizeBytes,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    bool IsMsi,
    ProgramScope Scope)
{
    public string SizeText => SizeBytes > 0 ? Humanize.Bytes(SizeBytes) : "";

    public string InstalledText => InstalledOn is { } date ? date.ToString("d") : "";

    public string ScopeText => Scope switch
    {
        ProgramScope.User => "User",
        ProgramScope.Machine32 => "All (32-bit)",
        _ => "All users",
    };

    public bool NeedsElevation => Scope != ProgramScope.User;

    public bool CanUninstall => UninstallString is { Length: > 0 };

    /// <summary>Registry path of the entry, for the leftover search.</summary>
    public string RegistryPath => Scope switch
    {
        ProgramScope.User => $@"HKCU\{SystemPrograms.UninstallPath}\{Key}",
        ProgramScope.Machine32 => $@"HKLM\{SystemPrograms.Uninstall32Path}\{Key}",
        _ => $@"HKLM\{SystemPrograms.UninstallPath}\{Key}",
    };
}

/// <summary>The installed programs, from the same source the Windows settings use.</summary>
/// <remarks>
/// <para>
/// Three keys: HKLM for all users, <c>Wow6432Node</c> for 32-bit programs, and HKCU for per-user
/// installs (Discord lives there).
/// </para>
/// <para>
/// <b>Filtered out:</b> hundreds of entries that aren't programs (updates, language packs, Visual C++
/// parts, child components), marked by <c>SystemComponent</c>, <c>ReleaseType</c> and
/// <c>ParentKeyName</c>.
/// </para>
/// </remarks>
internal static class SystemPrograms
{
    public const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string Uninstall32Path = @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledProgram> Read()
    {
        var found = new List<InstalledProgram>();

        Read(found, Registry.LocalMachine, UninstallPath, ProgramScope.Machine);
        Read(found, Registry.LocalMachine, Uninstall32Path, ProgramScope.Machine32);
        Read(found, Registry.CurrentUser, UninstallPath, ProgramScope.User);

        // The same program can be under HKLM and HKCU, e.g. a "just me" install over an older "everyone".
        return [.. found
            .GroupBy(p => $"{p.Name}|{p.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static void Read(List<InstalledProgram> into, RegistryKey root, string path, ProgramScope scope)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;

            foreach (var name in key.GetSubKeyNames())
            {
                using var entry = key.OpenSubKey(name);
                if (entry is null) continue;

                if (entry.GetValue("DisplayName") is not string display || display.Length == 0) continue;

                // A system component is by definition not removed on its own, and trying often breaks Windows.
                if (entry.GetValue("SystemComponent") is int component && component != 0) continue;

                // Updates, hotfixes and language packs.
                if (entry.GetValue("ReleaseType") is string release
                    && release.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;

                // Part of another entry; the parent is in the list anyway.
                if (entry.GetValue("ParentKeyName") is string { Length: > 0 }) continue;

                into.Add(new InstalledProgram(
                    Key: name,
                    Name: display,
                    Publisher: entry.GetValue("Publisher") as string,
                    Version: entry.GetValue("DisplayVersion") as string,
                    InstalledOn: ParseDate(entry.GetValue("InstallDate") as string),

                    // EstimatedSize is in kilobytes; an unchecked multiplication would overflow on a broken entry.
                    SizeBytes: entry.GetValue("EstimatedSize") is int kilobytes && kilobytes > 0
                        ? (long)kilobytes * 1024
                        : 0,

                    InstallLocation: Clean(entry.GetValue("InstallLocation") as string),
                    UninstallString: entry.GetValue("UninstallString") as string,
                    QuietUninstallString: entry.GetValue("QuietUninstallString") as string,
                    IsMsi: entry.GetValue("WindowsInstaller") is int msi && msi != 0,
                    Scope: scope));
            }
        }
        catch (Exception)
        {
            // An unreadable branch costs its entries, not the whole list.
        }
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyyMMdd", null,
            System.Globalization.DateTimeStyles.None, out var date) ? date : null;

    private static string? Clean(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim().TrimEnd('\\');

    /// <summary>Starts the program's uninstaller.</summary>
    /// <remarks>
    /// <para>
    /// <b>Through the shell, not directly.</b> Almost every uninstaller requests admin rights in its
    /// manifest; <c>UseShellExecute = false</c> from an unelevated process fails with error 740, while the
    /// shell shows the usual prompt.
    /// </para>
    /// <para>
    /// <b>Visible, not quiet.</b> <c>QuietUninstallString</c> would remove the program without the choices
    /// some uninstallers offer (keep settings, keep data). That decision belongs to the user.
    /// </para>
    /// </remarks>
    public static Process? StartUninstall(InstalledProgram program)
    {
        if (program.UninstallString is not { Length: > 0 } command) return null;

        var (exe, arguments) = Split(command);

        // Some packages register MsiExec with /I (install) instead of /X (remove), which opens the
        // repair dialog instead of uninstalling.
        if (Path.GetFileNameWithoutExtension(exe).Equals("msiexec", StringComparison.OrdinalIgnoreCase))
            arguments = MsiRemove(arguments);

        var info = new ProcessStartInfo(exe)
        {
            Arguments = arguments,
            UseShellExecute = true,
        };

        return Process.Start(info);
    }

    private static string MsiRemove(string arguments)
    {
        foreach (var flag in new[] { "/I", "/i" })
        {
            var at = arguments.IndexOf(flag, StringComparison.Ordinal);
            if (at < 0) continue;

            return arguments.Remove(at, 2).Insert(at, "/X");
        }

        return arguments;
    }

    /// <summary>Splits a command line into program and arguments.</summary>
    private static (string Exe, string Arguments) Split(string command)
    {
        var text = command.Trim();

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1) return (text[1..end], text[(end + 1)..].Trim());
        }

        // Without quotes the path ends at the extension, the rule Windows itself follows; splitting at
        // the first space would break "C:\Program Files\…".
        var extension = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (extension > 0) return (text[..(extension + 4)], text[(extension + 4)..].Trim());

        var space = text.IndexOf(' ');
        return space > 0 ? (text[..space], text[(space + 1)..]) : (text, "");
    }
}
