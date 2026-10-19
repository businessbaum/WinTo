using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiTool.Modules.Storage.Cleanup;

/// <summary>Something that can be cleaned: a set of folders whose content is disposable.</summary>
/// <param name="Folders">Roots whose content goes; the roots themselves stay.</param>
/// <param name="Pattern">Only matching files, e.g. <c>thumbcache_*.db</c>; <c>*</c> for everything.</param>
/// <param name="MinAge">Files changed more recently are kept, e.g. an installer still unpacking into Temp.</param>
/// <param name="Processes">Programs that must be closed first; their caches are in use while they run.</param>
public sealed record CleanupTarget(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<string> Folders,
    string Pattern = "*",
    TimeSpan MinAge = default,
    IReadOnlyList<string>? Processes = null,
    bool Default = true,
    bool RecycleBin = false);

/// <summary>What a run found or removed.</summary>
public sealed record CleanupResult(long Bytes, int Files, int Skipped);

/// <summary>Measures and empties the cleanup targets.</summary>
/// <remarks>
/// <para>
/// <b>Deletes, it doesn't recycle.</b> Temp files and caches in the recycle bin would take the same space.
/// Only below the fixed roots of <see cref="Targets"/>, never a root itself.
/// </para>
/// <para>
/// <b>Never follows a link.</b> A junction inside Temp may point anywhere; reparse points are skipped while
/// walking, as files and as folders.
/// </para>
/// <para>
/// <b>In use is not an error.</b> A file another program holds is skipped and counted; the rest goes.
/// </para>
/// </remarks>
internal static partial class Cleaner
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static IReadOnlyList<CleanupTarget> Targets { get; } = Build();

    private static List<CleanupTarget> Build()
    {
        var targets = new List<CleanupTarget>
        {
            new("temp", "Temporary files", "Left behind by installers and programs. Files from the last 24 hours stay.",
                [Path.GetTempPath()], MinAge: TimeSpan.FromDays(1)),
        };

        // One row per installed browser: a running Edge must not hold back Chrome's cache.
        var chromium = new (string Id, string Name, string Root, string Process)[]
        {
            ("chrome", "Chrome", Path.Combine(Local, "Google", "Chrome", "User Data"), "chrome"),
            ("edge", "Edge", Path.Combine(Local, "Microsoft", "Edge", "User Data"), "msedge"),
            ("brave", "Brave", Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data"), "brave"),
            ("opera", "Opera", Path.Combine(Roaming, "Opera Software", "Opera Stable"), "opera"),
        };

        foreach (var (id, name, root, process) in chromium)
        {
            if (!Directory.Exists(root)) continue;

            var folders = Profiles(root)
                .SelectMany(p => new[] { Path.Combine(p, "Cache"), Path.Combine(p, "Code Cache"), Path.Combine(p, "GPUCache") })
                .ToList();

            targets.Add(new($"browser-{id}", $"{name} cache", "Pages and pictures kept for faster loading. Pages load a little slower at first.",
                folders, Processes: [process]));
        }

        var firefox = Path.Combine(Local, "Mozilla", "Firefox", "Profiles");
        if (Directory.Exists(firefox))
        {
            targets.Add(new("browser-firefox", "Firefox cache", "Pages and pictures kept for faster loading. Pages load a little slower at first.",
                [.. SafeDirectories(firefox).Select(p => Path.Combine(p, "cache2"))], Processes: ["firefox"]));
        }

        targets.AddRange(
        [
            new("discord", "Discord cache", "Pictures and videos Discord downloaded for chats.",
                [Path.Combine(Roaming, "discord", "Cache"), Path.Combine(Roaming, "discord", "Code Cache"),
                 Path.Combine(Roaming, "discord", "GPUCache")],
                Processes: ["Discord"]),

            new("crash", "Crash dumps and error reports", "Memory dumps of crashed programs and Windows error reports.",
                [Path.Combine(Local, "CrashDumps"), Path.Combine(Local, "Microsoft", "Windows", "WER")]),

            new("thumbnails", "Thumbnail cache", "Explorer's previews of pictures and videos. Rebuilt when folders are opened.",
                [Path.Combine(Local, "Microsoft", "Windows", "Explorer")], Pattern: "thumbcache_*.db"),

            new("shaders", "Shader caches", "DirectX, NVIDIA and AMD. Rebuilt by games; the first minutes of a game may stutter.",
                [Path.Combine(Local, "D3DSCache"), Path.Combine(Local, "NVIDIA", "DXCache"),
                 Path.Combine(Local, "NVIDIA", "GLCache"), Path.Combine(Local, "AMD", "DxCache")],
                Default: false),

            new("recycle", "Recycle bin", "Everything deleted to the recycle bin, on every drive.",
                [], Default: false, RecycleBin: true),
        ]);

        return targets;
    }

    private static IEnumerable<string> Profiles(string userData)
    {
        foreach (var directory in SafeDirectories(userData))
        {
            var name = Path.GetFileName(directory);
            if (name == "Default" || name.StartsWith("Profile ", StringComparison.Ordinal)) yield return directory;
        }

        // Opera keeps its cache next to its settings, without profiles.
        if (File.Exists(Path.Combine(userData, "Preferences"))) yield return userData;
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try
        {
            return Directory.GetDirectories(folder);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Programs of <paramref name="target"/> that run right now.</summary>
    public static List<string> Running(CleanupTarget target)
    {
        var running = new List<string>();
        foreach (var name in target.Processes ?? [])
        {
            var processes = Process.GetProcessesByName(name);
            if (processes.Length > 0) running.Add(name);
            foreach (var process in processes) process.Dispose();
        }

        return running;
    }

    public static CleanupResult Measure(CleanupTarget target, CancellationToken cancel) =>
        target.RecycleBin ? MeasureRecycleBin() : Walk(target, delete: false, cancel);

    public static CleanupResult Clean(CleanupTarget target, CancellationToken cancel) =>
        target.RecycleBin ? EmptyRecycleBin() : Walk(target, delete: true, cancel);

    private static CleanupResult Walk(CleanupTarget target, bool delete, CancellationToken cancel)
    {
        long bytes = 0;
        var files = 0;
        var skipped = 0;
        var cutoff = DateTime.UtcNow - target.MinAge;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        foreach (var root in target.Folders)
        {
            if (!Directory.Exists(root) || IsLink(root)) continue;

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFiles(root, target.Pattern, options);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var path in entries)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc > cutoff) continue;

                    var length = info.Length;
                    if (delete)
                    {
                        if (info.IsReadOnly) info.IsReadOnly = false;
                        info.Delete();
                    }

                    bytes += length;
                    files++;
                }
                catch (Exception)
                {
                    skipped++;
                }
            }

            if (delete && target.Pattern == "*") RemoveEmptyFolders(root, options);
        }

        return new CleanupResult(bytes, files, skipped);
    }

    /// <summary>Folders left empty go too, deepest first; the root stays.</summary>
    private static void RemoveEmptyFolders(string root, EnumerationOptions options)
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root, "*", options).OrderByDescending(f => f.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
                }
                catch (Exception)
                {
                    // In use or not empty after all.
                }
            }
        }
        catch (Exception)
        {
            // The files are gone; empty folders cost nothing.
        }
    }

    private static bool IsLink(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).LinkTarget is not null;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // --------------------------------------------------------------- Recycle bin

    /// <remarks>Walks every item and can take half a minute with thousands; never on the UI thread.</remarks>
    private static CleanupResult MeasureRecycleBin()
    {
        var info = new QueryInfo { Size = Marshal.SizeOf<QueryInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0
            ? new CleanupResult(info.Bytes, (int)Math.Min(info.Items, int.MaxValue), 0)
            : new CleanupResult(0, 0, 0);
    }

    private static CleanupResult EmptyRecycleBin()
    {
        var before = MeasureRecycleBin();

        // No confirmation, no progress window, no sound: the view asked already.
        const uint flags = 0x1 | 0x2 | 0x4;
        var result = SHEmptyRecycleBinW(0, null, flags);
        return result is 0 or unchecked((int)0x8000FFFF) ? before : new CleanupResult(0, 0, before.Files);
    }

    /// <summary>SHQUERYRBINFO: on x64 naturally aligned, the sizes start at offset 8 (24 bytes in all).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? root, ref QueryInfo info);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(nint window, string? root, uint flags);
}
