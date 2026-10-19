using Microsoft.Win32;
using MultiTool.Abstractions;

namespace MultiTool.Modules.Apps.Uninstaller;

public enum LeftoverKind { Folder, RegistryKey }

/// <param name="High">The name matches exactly, or it is the registered install location.</param>
public enum Confidence { High, Medium }

public sealed record Leftover(
    LeftoverKind Kind,
    string Path,
    string Reason,
    Confidence Confidence,
    long SizeBytes)
{
    public string KindText => Kind == LeftoverKind.Folder ? "Folder" : "Registry";

    public string SizeText => SizeBytes > 0 ? Humanize.Bytes(SizeBytes) : "";

    public bool IsHigh => Confidence == Confidence.High;
}

/// <summary>Finds what remains after an uninstall.</summary>
/// <remarks>
/// <para>
/// A name search in the places programs keep their data, <i>not</i> a before/after comparison of the
/// system (that would need a full snapshot taken before installing). The UI says so as well.
/// </para>
/// <para>
/// <b>Never by publisher alone:</b> for "Google LLC", <c>HKCU\Software\Google</c> holds the data of
/// <i>every</i> Google program. The publisher name is only used to look one level deeper
/// (<c>Program Files\Google\Chrome</c>), never as a match by itself.
/// </para>
/// <para>
/// <b>Nothing disappears without asking:</b> suggestions only, folders go to the recycle bin, and a
/// <c>.reg</c> backup is written before deleting registry keys.
/// </para>
/// </remarks>
internal static class LeftoverScan
{
    /// <summary>Places never suggested themselves, even on a name match.</summary>
    private static readonly string[] Untouchable =
    [
        "common files", "windowsapps", "microsoft", "classes", "windows",
        "policies", "clients", "wow6432node", "intel", "nvidia corporation",
    ];

    public static List<Leftover> Scan(InstalledProgram program, CancellationToken cancellationToken)
    {
        var found = new List<Leftover>();
        var normalized = Normalize(program.Name);

        // A too short name matches everything. "VLC" still works, "Go" doesn't.
        if (normalized.Length < 3) return found;

        var publisher = program.Publisher is null ? null : Normalize(program.Publisher);

        ScanFolders(found, program, normalized, publisher, cancellationToken);
        ScanRegistry(found, program, normalized, publisher, cancellationToken);

        return [.. found
            .DistinctBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(l => l.Confidence == Confidence.High)
            .ThenByDescending(l => l.SizeBytes)];
    }

    // ------------------------------------------------------------------ Files

    private static void ScanFolders(
        List<Leftover> into, InstalledProgram program, string normalized, string? publisher,
        CancellationToken cancellationToken)
    {
        // The registered install location is the only certain match: it comes from the program itself.
        if (program.InstallLocation is { } location && Directory.Exists(location) && !IsUntouchable(location))
        {
            into.Add(new Leftover(LeftoverKind.Folder, location,
                "Registered install location, still present.", Confidence.High, SizeOf(location, cancellationToken)));
        }

        foreach (var root in FolderRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;

            foreach (var directory in SafeDirectories(root))
            {
                var name = Path.GetFileName(directory);

                if (Matches(name, normalized) is { } confidence && !IsUntouchable(directory))
                {
                    into.Add(new Leftover(LeftoverKind.Folder, directory,
                        $"Folder name matches the program ({root}).", confidence,
                        SizeOf(directory, cancellationToken)));

                    continue;
                }

                // One level deeper if the folder is named after the publisher.
                if (publisher is null || Normalize(name) != publisher) continue;

                foreach (var child in SafeDirectories(directory))
                {
                    if (Matches(Path.GetFileName(child), normalized) is not { } childConfidence) continue;
                    if (IsUntouchable(child)) continue;

                    into.Add(new Leftover(LeftoverKind.Folder, child,
                        $"In the publisher's folder ({name}).", childConfidence,
                        SizeOf(child, cancellationToken)));
                }
            }
        }
    }

    private static IEnumerable<string> FolderRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Where modern installers write without admin rights: Discord, VS Code, many Electron apps.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Size of a folder, capped.</summary>
    /// <remarks>
    /// A leftover can be a cache with a hundred thousand files; the order of magnitude is enough, so
    /// counting stops after twenty thousand files.
    /// </remarks>
    private static long SizeOf(string directory, CancellationToken cancellationToken)
    {
        try
        {
            long total = 0;
            var counted = 0;

            foreach (var file in Directory.EnumerateFiles(directory, "*",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.ReparsePoint,
                         }))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try { total += new FileInfo(file).Length; }
                catch (Exception) { }

                if (++counted >= 20_000) break;
            }

            return total;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ Registry

    private static void ScanRegistry(
        List<Leftover> into, InstalledProgram program, string normalized, string? publisher,
        CancellationToken cancellationToken)
    {
        // The program's own uninstall entry. Still there after removal means it's either orphaned or
        // the program is still installed; either way it belongs in the list.
        into.Add(new Leftover(LeftoverKind.RegistryKey, program.RegistryPath,
            "The program's uninstall entry.", Confidence.High, 0));

        (RegistryKey Root, string Path, string Label)[] roots =
        [
            (Registry.CurrentUser, "Software", "HKCU\\Software"),
            (Registry.LocalMachine, "SOFTWARE", "HKLM\\SOFTWARE"),
            (Registry.LocalMachine, @"SOFTWARE\Wow6432Node", "HKLM\\SOFTWARE\\Wow6432Node"),
        ];

        foreach (var (root, path, label) in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) continue;

                foreach (var name in key.GetSubKeyNames())
                {
                    if (Matches(name, normalized) is { } confidence && !IsUntouchable(name))
                    {
                        into.Add(new Leftover(LeftoverKind.RegistryKey, $@"{label}\{name}",
                            "Key name matches the program.", confidence, 0));

                        continue;
                    }

                    if (publisher is null || Normalize(name) != publisher) continue;

                    using var vendor = key.OpenSubKey(name);
                    if (vendor is null) continue;

                    foreach (var child in vendor.GetSubKeyNames())
                    {
                        if (Matches(child, normalized) is not { } childConfidence) continue;
                        if (IsUntouchable(child)) continue;

                        into.Add(new Leftover(LeftoverKind.RegistryKey, $@"{label}\{name}\{child}",
                            $"In the publisher's key ({name}).", childConfidence, 0));
                    }
                }
            }
            catch (Exception)
            {
                // A branch without read access costs its matches.
            }
        }
    }

    // ------------------------------------------------------------------ Names

    /// <summary>Turns "Mozilla Firefox (x64 en-US)" into a comparable "mozillafirefox".</summary>
    /// <remarks>Versions, parenthesized additions and separators are in display names but rarely in folder names.</remarks>
    private static string Normalize(string value)
    {
        var open = value.IndexOf('(');
        if (open > 0) value = value[..open];

        return new string([.. value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)]);
    }

    /// <returns><c>null</c> if the name doesn't match.</returns>
    private static Confidence? Matches(string candidate, string normalized)
    {
        var name = Normalize(candidate);
        if (name.Length < 3) return null;

        if (name == normalized) return Confidence.High;

        // Containment is weaker and allowed both ways: "Firefox" folder for "Mozilla Firefox", and
        // "VLCPlayer" for "VLC".
        if (name.Contains(normalized, StringComparison.Ordinal)
            || normalized.Contains(name, StringComparison.Ordinal))
        {
            return Confidence.Medium;
        }

        return null;
    }

    private static bool IsUntouchable(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName.TrimEnd('\\'));
        if (Untouchable.Contains(name.ToLowerInvariant())) return true;

        // A full path is also checked against protected places; a bare name (a registry key) has no place.
        return Path.IsPathFullyQualified(pathOrName) && IsProtectedLocation(pathOrName);
    }

    /// <summary>
    /// Places that never count as leftovers, whatever the registry says (see <see cref="ProtectedPaths"/>).
    /// Some installers register <c>C:\Program Files</c> or the user profile as their location.
    /// </summary>
    internal static bool IsProtectedLocation(string path) => ProtectedPaths.IsProtected(path);

    // ------------------------------------------------------------------ removing

    /// <summary>Writes a <c>.reg</c> backup of the affected keys.</summary>
    /// <remarks>
    /// Via <c>reg.exe export</c>, not by hand: the format knows strings, numbers, multi-strings and binary
    /// data, and a home-made exporter would get exactly the value wrong that matters later. Double-click
    /// restores everything.
    /// </remarks>
    public static async Task<RegistryBackup> BackupRegistryAsync(
        IReadOnlyList<Leftover> keys, string targetFile, CancellationToken cancellationToken)
    {
        var registryKeys = keys.Where(k => k.Kind == LeftoverKind.RegistryKey).ToList();
        if (registryKeys.Count == 0) return new RegistryBackup(null, []);

        var parts = new List<string>();
        var failed = new List<Leftover>();

        for (var i = 0; i < registryKeys.Count; i++)
        {
            var part = $"{targetFile}.{i}";

            var result = await ExternalProcess.RunAsync("reg.exe",
                ["export", registryKeys[i].Path, part, "/y"], cancellationToken: cancellationToken);

            // A key whose export fails isn't in the backup and must not be deleted; the caller gets the list.
            if (result.Ok && File.Exists(part)) parts.Add(part);
            else failed.Add(registryKeys[i]);
        }

        if (parts.Count == 0) return new RegistryBackup(null, failed);

        // reg.exe writes a complete file with header per call; combined, the header belongs once at the top.
        var lines = new List<string> { "Windows Registry Editor Version 5.00", "" };

        foreach (var part in parts)
        {
            foreach (var line in await File.ReadAllLinesAsync(part, cancellationToken))
            {
                if (line.StartsWith("Windows Registry Editor", StringComparison.Ordinal)) continue;
                lines.Add(line);
            }

            try { File.Delete(part); } catch (Exception) { }
        }

        // .reg files are UTF-16 with BOM; otherwise the Registry Editor rejects them.
        await File.WriteAllLinesAsync(targetFile, lines, System.Text.Encoding.Unicode, cancellationToken);
        return new RegistryBackup(targetFile, failed);
    }

    /// <param name="File">The written <c>.reg</c> file, or <c>null</c>.</param>
    /// <param name="Failed">Keys whose export failed; deleting them would have no safety net.</param>
    public sealed record RegistryBackup(string? File, IReadOnlyList<Leftover> Failed);

    public static (int Removed, List<string> Problems) Remove(IReadOnlyList<Leftover> items)
    {
        var problems = new List<string>();
        var removed = 0;

        // Collect folders and move them in one operation, not one per folder. Second check where the
        // deleting happens: a protected place must not reach the recycle bin, however it got into the list.
        foreach (var item in items.Where(i => i.Kind == LeftoverKind.Folder && IsProtectedLocation(i.Path)))
            problems.Add($"{item.Path}: protected location, not removed.");

        var folders = items
            .Where(i => i.Kind == LeftoverKind.Folder && !IsProtectedLocation(i.Path) && Directory.Exists(i.Path))
            .Select(i => i.Path)
            .ToList();

        if (folders.Count > 0)
        {
            var problem = RecycleBin.Delete(folders);

            // Recount even after an error: SHFileOperation continues past a stuck folder, and the others
            // are in the recycle bin and must not show as "not removed".
            var gone = folders.Count(f => !Directory.Exists(f));
            removed += gone;

            if (problem is not null && gone < folders.Count)
                problems.Add($"Folders: {problem}");
        }

        foreach (var item in items.Where(i => i.Kind == LeftoverKind.RegistryKey))
        {
            try
            {
                var (root, path) = SplitRegistryPath(item.Path);
                if (root is null) continue;

                root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                removed++;
            }
            catch (UnauthorizedAccessException)
            {
                problems.Add($"{item.Path}: no rights, elevated start needed.");
            }
            catch (Exception ex)
            {
                problems.Add($"{item.Path}: {ex.Message}");
            }
        }

        return (removed, problems);
    }

    /// <summary>Whether the leftover still exists; the only honest answer after removing.</summary>
    public static bool Exists(Leftover item)
    {
        try
        {
            if (item.Kind == LeftoverKind.Folder) return Directory.Exists(item.Path);

            var (root, path) = SplitRegistryPath(item.Path);
            if (root is null) return false;

            using var key = root.OpenSubKey(path);
            return key is not null;
        }
        catch (Exception)
        {
            // No access means: not removed.
            return true;
        }
    }

    private static (RegistryKey? Root, string Path) SplitRegistryPath(string full)
    {
        var separator = full.IndexOf('\\');
        if (separator < 0) return (null, "");

        var hive = full[..separator];
        var path = full[(separator + 1)..];

        return hive switch
        {
            "HKCU" => (Registry.CurrentUser, path),
            "HKLM" => (Registry.LocalMachine, path),
            _ => (null, ""),
        };
    }
}
